# Audio Source Mixer User Guide

[简体中文](USER_GUIDE.zh-CN.md)

## Basic use

Install and launch Audio Source Mixer, then use each source card to control volume, mute, balance, output, and—where supported—EQ. Windows sources use the native 0–100% range; enhanced browser tabs support up to 200% gain. Values above 100% may distort.

The peak bar is a display-only live level. Closing the main window can keep the app in the tray. Use **Exit and restore audio** from the tray to restore controlled audio state before shutdown.

Drag a card by its six-dot handle to reorder it. The card menu can move or hide a source. **Hidden N** restores one or all manually hidden sources; automatic browser aggregate filtering is a separate setting.

## Output routing

For Windows sessions, the app reports the real routing result: applied, failed, disconnected, partial, or pending stream restart. A pending restart means the preference is saved but the application must recreate its audio stream.

For enhanced tabs, selecting a new physical output may open the authorization page. Select the same device shown by Windows, play the short test tone, and confirm it. A device rename, reconnect, or browser identifier change may require reauthorization. The app never treats a silent fallback as success.

## Browser enhancement

1. Keep the installed desktop app running or in the tray.
2. In the app, open **Browser enhancement** and open the appropriate extension management page.
3. Enable Developer mode, choose **Load unpacked**, and select the installed `BrowserExtension` directory.
4. Start playback in a tab and click the extension action. Click again to stop enhancement.

The first action may open the local welcome page. Read it, return to the playing tab, and click again. The extension only processes a tab after this user action.

## Language and accessibility

Open **Settings → Language** and choose Simplified Chinese or English. The current window, instantiated templates, status messages, and tray menu update immediately without recreating the audio engine or resetting source state. The choice is persisted independently from the product version.

Keyboard focus, screen-reader names, high-contrast colors, and Windows DPI scaling are supported. At the 880×600 minimum size, use the main page scrollbar for content that does not fit.

## Data and uninstall

Desktop data is stored in `%LocalAppData%\AudioSourceMixer`; extension mappings remain inside the current browser profile. Uninstalling retains desktop data by default. Select **Remove user settings and logs** only when you want that directory deleted. Extension mappings can be cleared from the authorization page or by removing the extension profile data.
