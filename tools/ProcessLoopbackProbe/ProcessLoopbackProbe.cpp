#include <Windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <wrl.h>
#include <wrl/implements.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::FtmBase;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;

namespace
{
    constexpr DWORD CaptureFlags = AUDCLNT_STREAMFLAGS_LOOPBACK |
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
    constexpr int SilenceThresholdPcm16 = 16;

    class Handle final
    {
    public:
        explicit Handle(HANDLE value = nullptr) noexcept : value_(value) {}
        ~Handle() { if (value_) CloseHandle(value_); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        HANDLE Get() const noexcept { return value_; }
    private:
        HANDLE value_;
    };

    class ActivationHandler final :
        public RuntimeClass<RuntimeClassFlags<ClassicCom>, FtmBase, IActivateAudioInterfaceCompletionHandler>
    {
    public:
        explicit ActivationHandler(HANDLE completed) noexcept : completed_(completed) {}

        STDMETHODIMP ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override
        {
            HRESULT activationResult = E_UNEXPECTED;
            ComPtr<IUnknown> activated;
            const HRESULT callbackResult = operation->GetActivateResult(&activationResult, &activated);
            result_ = FAILED(callbackResult) ? callbackResult : activationResult;
            activated_ = activated;
            SetEvent(completed_);
            return S_OK;
        }

        HRESULT Result() const noexcept { return result_; }
        IUnknown* Activated() const noexcept { return activated_.Get(); }

    private:
        HANDLE completed_;
        HRESULT result_ = E_UNEXPECTED;
        ComPtr<IUnknown> activated_;
    };

    class WaveWriter final
    {
    public:
        WaveWriter(const wchar_t* path, const WAVEFORMATEX& format) : stream_(path, std::ios::binary)
        {
            if (!stream_) return;
            stream_.write("RIFF", 4);
            Write32(0);
            stream_.write("WAVEfmt ", 8);
            Write32(16);
            Write16(WAVE_FORMAT_PCM);
            Write16(format.nChannels);
            Write32(format.nSamplesPerSec);
            Write32(format.nAvgBytesPerSec);
            Write16(format.nBlockAlign);
            Write16(format.wBitsPerSample);
            stream_.write("data", 4);
            Write32(0);
        }

        bool IsOpen() const noexcept { return stream_.is_open() && stream_.good(); }

        bool Write(const BYTE* bytes, DWORD count)
        {
            stream_.write(reinterpret_cast<const char*>(bytes), count);
            dataBytes_ += count;
            return stream_.good();
        }

        bool WriteSilence(DWORD count)
        {
            std::vector<BYTE> zeroes(count);
            return Write(zeroes.data(), count);
        }

        bool Finalize()
        {
            if (!stream_.is_open()) return false;
            stream_.seekp(4, std::ios::beg);
            Write32(dataBytes_ + 36);
            stream_.seekp(40, std::ios::beg);
            Write32(dataBytes_);
            stream_.flush();
            return stream_.good();
        }

    private:
        void Write16(std::uint16_t value) { stream_.write(reinterpret_cast<const char*>(&value), sizeof(value)); }
        void Write32(std::uint32_t value) { stream_.write(reinterpret_cast<const char*>(&value), sizeof(value)); }

        std::ofstream stream_;
        std::uint32_t dataBytes_ = 0;
    };

    struct Statistics
    {
        std::uint64_t frames = 0;
        std::uint64_t samples = 0;
        std::uint64_t nonSilentFrames = 0;
        long double sumSquares = 0;
        double peak = 0;

        void AddPcm16(const BYTE* data, UINT32 frameCount, UINT16 channels, bool silent)
        {
            const auto* pcm = reinterpret_cast<const std::int16_t*>(data);
            for (UINT32 frame = 0; frame < frameCount; ++frame)
            {
                bool nonSilent = false;
                for (UINT16 channel = 0; channel < channels; ++channel)
                {
                    const std::int16_t value = silent ? 0 : pcm[static_cast<std::size_t>(frame) * channels + channel];
                    const double normalized = std::abs(static_cast<double>(value) / 32768.0);
                    sumSquares += normalized * normalized;
                    peak = std::max(peak, normalized);
                    nonSilent = nonSilent || std::abs(static_cast<int>(value)) > SilenceThresholdPcm16;
                    ++samples;
                }
                if (nonSilent) ++nonSilentFrames;
                ++frames;
            }
        }

        double Rms() const { return samples == 0 ? 0 : std::sqrt(static_cast<double>(sumSquares / samples)); }
    };

    void PrintHresult(const wchar_t* operation, HRESULT result)
    {
        std::wcout << operation << L" hresult=0x" << std::hex << std::setw(8) << std::setfill(L'0')
            << static_cast<unsigned long>(result) << std::dec << std::setfill(L' ') << L"\n";
    }

    void PrintOsVersion()
    {
        using RtlGetVersionFunction = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
        const auto ntdll = GetModuleHandleW(L"ntdll.dll");
        const auto rtlGetVersion = reinterpret_cast<RtlGetVersionFunction>(GetProcAddress(ntdll, "RtlGetVersion"));
        RTL_OSVERSIONINFOW version{ sizeof(version) };
        if (rtlGetVersion && rtlGetVersion(&version) == 0)
        {
            std::wcout << L"OS_VERSION major=" << version.dwMajorVersion << L" minor=" << version.dwMinorVersion
                << L" build=" << version.dwBuildNumber << L" architecture=x64\n";
        }
    }

    HRESULT ActivateProcessLoopback(
        DWORD processId,
        bool includeTree,
        AUDCLNT_STREAMOPTIONS options,
        ComPtr<IAudioClient>& audioClient)
    {
        Handle completed(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        if (!completed.Get()) return HRESULT_FROM_WIN32(GetLastError());

        AUDIOCLIENT_ACTIVATION_PARAMS activation{};
        activation.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        activation.ProcessLoopbackParams.TargetProcessId = processId;
        activation.ProcessLoopbackParams.ProcessLoopbackMode = includeTree
            ? PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            : PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;

        PROPVARIANT parameters{};
        parameters.vt = VT_BLOB;
        parameters.blob.cbSize = sizeof(activation);
        parameters.blob.pBlobData = reinterpret_cast<BYTE*>(&activation);

        auto handler = Microsoft::WRL::Make<ActivationHandler>(completed.Get());
        if (!handler) return E_OUTOFMEMORY;
        ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
        const IID& requested = options == AUDCLNT_STREAMOPTIONS_NONE ? __uuidof(IAudioClient) : __uuidof(IAudioClient2);
        const HRESULT callResult = ActivateAudioInterfaceAsync(
            VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            requested,
            &parameters,
            handler.Get(),
            &operation);
        std::wcout << L"ACTIVATE_CALL requested="
            << (options == AUDCLNT_STREAMOPTIONS_NONE ? L"IAudioClient" : L"IAudioClient2") << L" ";
        PrintHresult(L"result", callResult);
        if (FAILED(callResult)) return callResult;
        if (WaitForSingleObject(completed.Get(), 10000) != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);

        const HRESULT activationResult = handler->Result();
        PrintHresult(L"ACTIVATE_COMPLETION", activationResult);
        if (FAILED(activationResult)) return activationResult;
        if (!handler->Activated()) return E_NOINTERFACE;

        HRESULT result = handler->Activated()->QueryInterface(IID_PPV_ARGS(&audioClient));
        PrintHresult(L"QUERY_IAUDIOCLIENT", result);
        if (FAILED(result)) return result;

        if (options != AUDCLNT_STREAMOPTIONS_NONE)
        {
            ComPtr<IAudioClient2> audioClient2;
            result = handler->Activated()->QueryInterface(IID_PPV_ARGS(&audioClient2));
            PrintHresult(L"QUERY_IAUDIOCLIENT2", result);
            if (FAILED(result)) return result;
            AudioClientProperties properties{};
            properties.cbSize = sizeof(properties);
            properties.eCategory = AudioCategory_Other;
            properties.Options = options;
            result = audioClient2->SetClientProperties(&properties);
            std::wcout << L"SET_CLIENT_PROPERTIES options=0x" << std::hex << static_cast<unsigned>(options) << std::dec << L" ";
            PrintHresult(L"result", result);
            if (FAILED(result)) return result;
        }
        else
        {
            std::wcout << L"SET_CLIENT_PROPERTIES options=0x0 status=default-not-called\n";
        }
        return S_OK;
    }

    HRESULT Capture(
        DWORD processId,
        bool includeTree,
        AUDCLNT_STREAMOPTIONS options,
        DWORD durationSeconds,
        const wchar_t* outputPath)
    {
        ComPtr<IAudioClient> audioClient;
        HRESULT result = ActivateProcessLoopback(processId, includeTree, options, audioClient);
        if (FAILED(result)) return result;

        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_PCM;
        format.nChannels = 2;
        format.nSamplesPerSec = 44100;
        format.wBitsPerSample = 16;
        format.nBlockAlign = format.nChannels * format.wBitsPerSample / 8;
        format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
        std::wcout << L"CAPTURE_FORMAT tag=" << format.wFormatTag << L" channels=" << format.nChannels
            << L" sampleRate=" << format.nSamplesPerSec << L" bits=" << format.wBitsPerSample
            << L" blockAlign=" << format.nBlockAlign << L"\n";
        std::wcout << L"CAPTURE_FLAGS value=0x" << std::hex << CaptureFlags << std::dec
            << L" loopback=true eventCallback=true autoConvertPcm=true\n";

        result = audioClient->Initialize(AUDCLNT_SHAREMODE_SHARED, CaptureFlags, 0, 0, &format, nullptr);
        PrintHresult(L"INITIALIZE", result);
        if (FAILED(result)) return result;

        Handle sampleReady(CreateEventW(nullptr, FALSE, FALSE, nullptr));
        if (!sampleReady.Get()) return HRESULT_FROM_WIN32(GetLastError());
        result = audioClient->SetEventHandle(sampleReady.Get());
        PrintHresult(L"SET_EVENT_HANDLE", result);
        if (FAILED(result)) return result;

        ComPtr<IAudioCaptureClient> captureClient;
        result = audioClient->GetService(IID_PPV_ARGS(&captureClient));
        PrintHresult(L"GET_CAPTURE_SERVICE", result);
        if (FAILED(result)) return result;

        WaveWriter writer(outputPath, format);
        if (!writer.IsOpen()) return HRESULT_FROM_WIN32(ERROR_OPEN_FAILED);

        result = audioClient->Start();
        PrintHresult(L"START", result);
        if (FAILED(result)) return result;

        Statistics statistics;
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(durationSeconds);
        while (std::chrono::steady_clock::now() < deadline)
        {
            WaitForSingleObject(sampleReady.Get(), 100);
            UINT32 packetFrames = 0;
            while (SUCCEEDED(result = captureClient->GetNextPacketSize(&packetFrames)) && packetFrames > 0)
            {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                UINT64 devicePosition = 0;
                UINT64 qpcPosition = 0;
                result = captureClient->GetBuffer(&data, &frames, &flags, &devicePosition, &qpcPosition);
                if (FAILED(result)) break;
                const bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                const DWORD bytes = frames * format.nBlockAlign;
                statistics.AddPcm16(data, frames, format.nChannels, silent);
                const bool wrote = silent ? writer.WriteSilence(bytes) : writer.Write(data, bytes);
                const HRESULT releaseResult = captureClient->ReleaseBuffer(frames);
                if (!wrote) result = E_FAIL;
                else if (FAILED(releaseResult)) result = releaseResult;
                if (FAILED(result)) break;
            }
            if (FAILED(result)) break;
        }

        const HRESULT stopResult = audioClient->Stop();
        PrintHresult(L"STOP", stopResult);
        if (SUCCEEDED(result) && FAILED(stopResult)) result = stopResult;
        if (!writer.Finalize() && SUCCEEDED(result)) result = E_FAIL;

        std::wcout << std::setprecision(12)
            << L"CAPTURE_STATS frames=" << statistics.frames
            << L" rms=" << statistics.Rms()
            << L" peak=" << statistics.peak
            << L" nonSilentFrames=" << statistics.nonSilentFrames
            << L" nonSilentPercent=" << (statistics.frames == 0 ? 0 : 100.0 * statistics.nonSilentFrames / statistics.frames)
            << L"\n";
        return result;
    }

    void Usage()
    {
        std::wcerr << L"Usage: ProcessLoopbackProbe <pid> <includetree|excludetree> <none|post-volume> <durationSeconds>=5..60 <output.wav>\n";
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 6) { Usage(); return 2; }
    const DWORD processId = wcstoul(argv[1], nullptr, 10);
    const bool includeTree = wcscmp(argv[2], L"includetree") == 0;
    if (processId == 0 || (!includeTree && wcscmp(argv[2], L"excludetree") != 0)) { Usage(); return 2; }
    AUDCLNT_STREAMOPTIONS options = AUDCLNT_STREAMOPTIONS_NONE;
    if (wcscmp(argv[3], L"post-volume") == 0) options = AUDCLNT_STREAMOPTIONS_POST_VOLUME_LOOPBACK;
    else if (wcscmp(argv[3], L"none") != 0) { Usage(); return 2; }
    const DWORD durationSeconds = wcstoul(argv[4], nullptr, 10);
    if (durationSeconds < 5 || durationSeconds > 60) { Usage(); return 2; }

    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult)) { PrintHresult(L"CO_INITIALIZE", comResult); return 1; }
    PrintOsVersion();
    std::wcout << L"CAPTURE_REQUEST pid=" << processId << L" processTree=" << (includeTree ? L"include" : L"exclude")
        << L" options=0x" << std::hex << static_cast<unsigned>(options) << std::dec
        << L" durationSeconds=" << durationSeconds << L" output=\"" << argv[5] << L"\"\n";

    const HRESULT result = Capture(processId, includeTree, options, durationSeconds, argv[5]);
    PrintHresult(L"FINAL_RESULT", result);
    if (FAILED(result))
    {
        std::wcout << L"FAILED_OBSERVATION_WINDOW seconds=" << durationSeconds << L" status=no-capture-interface\n";
        std::this_thread::sleep_for(std::chrono::seconds(durationSeconds));
    }
    CoUninitialize();
    return FAILED(result) ? 1 : 0;
}
