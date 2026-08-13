# ADR 003：浏览器标签页控制

状态：最佳努力实现。

采用 Chrome/Edge MV3：用户点击 action 后，service worker 获取一次性 tabCapture stream ID，offscreen document 用 `getUserMedia` 建立 `MediaStreamAudioSourceNode → GainNode → StereoPannerNode → AnalyserNode → destination`。连接到 destination 用于恢复捕获后被浏览器移交给扩展的可听播放，不创建第二条独立播放路径。

控制/状态通过 Native Messaging Host 转为当前用户专用 Named Pipe。PCM 和网页正文不进入 IPC。扩展使用 manifest `key` 固定 ID，Native Host manifest 只允许该 ID。

固定用户手势、DRM 和浏览器生命周期限制不能绕过；P0 与此模块解耦。
