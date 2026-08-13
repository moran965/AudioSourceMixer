const parameters = new URLSearchParams(location.search);
const label = parameters.get('label') || 'A';
const requestedFrequency = Number(parameters.get('frequency'));
const frequency = Number.isFinite(requestedFrequency) && requestedFrequency >= 100 && requestedFrequency <= 4000
  ? requestedFrequency : 440;

const heading = document.querySelector('#heading');
const description = document.querySelector('#description');
const toggle = document.querySelector('#toggle');
const status = document.querySelector('#status');
document.title = `Route Matrix ${label} · ${frequency} Hz`;
heading.textContent = `标签页 ${label}`;
description.textContent = `持续正弦测试音：${frequency} Hz。只在按钮点击后启动。`;

let context;
let oscillator;
let gain;

toggle.addEventListener('click', async () => {
  if (context) {
    await stopTone();
    return;
  }

  context = new AudioContext();
  oscillator = context.createOscillator();
  gain = context.createGain();
  oscillator.frequency.value = frequency;
  gain.gain.value = 0.025;
  oscillator.connect(gain).connect(context.destination);
  oscillator.start();
  await context.resume();
  toggle.dataset.running = 'true';
  toggle.textContent = '停止测试音';
  status.textContent = `正在播放 ${frequency} Hz；AudioContext=${context.state}`;
});

async function stopTone() {
  const activeContext = context;
  context = undefined;
  try { oscillator?.stop(); } catch { /* Already stopped. */ }
  oscillator?.disconnect();
  gain?.disconnect();
  oscillator = undefined;
  gain = undefined;
  await activeContext?.close();
  toggle.dataset.running = 'false';
  toggle.textContent = '启动测试音';
  status.textContent = '已停止';
}

addEventListener('pagehide', () => { void stopTone(); }, { once: true });
