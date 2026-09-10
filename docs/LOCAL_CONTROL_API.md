# Local Bluetooth HID control API

This optional API lets a trusted local automation client send commands through
the existing `BluetoothHidMouseService`. It does not synthesize Windows mouse
or keyboard input.

## Enable it

The API is disabled by default. Set a private token in the environment that
starts iPhoneMirror:

```powershell
$env:IPHONE_MIRROR_CONTROL_TOKEN = [guid]::NewGuid().ToString('N')
$env:IPHONE_MIRROR_CONTROL_PORT = '17321' # optional; this is the default
Start-Process .\iPhoneMirror.exe
```

The server binds only to `127.0.0.1`. Every request must carry either
`Authorization: Bearer <token>` or `X-iPhoneMirror-Token: <token>`.

Before sending commands, start Bluetooth reverse control in iPhoneMirror and
finish the existing iPhone/iPad pairing and client-binding flow. `GET
/v1/status` returns `ready: true` when commands are accepted.

While the local API listener is running, iPhoneMirror uses hybrid control mode.
Moving the physical mouse over an actively controlled phone preview forwards
movement, clicks, and wheel input to iOS. Moving outside the preview leaves the
Windows desktop under normal mouse control. Click the preview before typing to
send keyboard input to iOS. Authenticated API commands can use the same
Bluetooth HID route at the same time.

Hybrid mode does not install process-wide raw input, hide the Windows cursor,
or suppress Windows system keys. Start iPhoneMirror without
`IPHONE_MIRROR_CONTROL_TOKEN` to retain the original full reverse-control
behavior.

```powershell
$base = 'http://127.0.0.1:17321'
$headers = @{ Authorization = "Bearer $env:IPHONE_MIRROR_CONTROL_TOKEN" }
Invoke-RestMethod "$base/v1/status" -Headers $headers

# Save the latest mirrored video frame for visual automation
Invoke-WebRequest "$base/v1/screenshot" -Headers $headers -OutFile phone.png
```

## Commands

All command endpoints use `POST` with a JSON body:

```powershell
# Relative HID pointer movement (not absolute screen coordinates)
Invoke-RestMethod "$base/v1/mouse/move" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"dx":120,"dy":-40}'

# Click: button is left, right, or middle; count is 1-3
Invoke-RestMethod "$base/v1/mouse/click" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"button":"left","count":1}'

# Wheel delta, from -1200 to 1200
Invoke-RestMethod "$base/v1/mouse/scroll" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"delta":-120}'

# Type US-keyboard ASCII text
Invoke-RestMethod "$base/v1/keyboard/text" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"text":"Hello, iPhone!"}'

# Press and release one USB HID keyboard usage (A = 4)
Invoke-RestMethod "$base/v1/keyboard/key" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"usage":4,"modifiers":2}'

# Send an exact keyboard state; [] releases all ordinary keys
Invoke-RestMethod "$base/v1/keyboard/report" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"modifiers":0,"usages":[4]}'

# Existing iPhone shortcuts
Invoke-RestMethod "$base/v1/system" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"action":"home"}'
Invoke-RestMethod "$base/v1/system" -Method Post -Headers $headers `
  -ContentType 'application/json' -Body '{"action":"app-switcher"}'
```

Supported system actions are `home`, `app-switcher`, `control-center`,
`notification-center`, `dock`, and `siri`.

`/v1/keyboard/text` deliberately supports only printable US-keyboard ASCII,
plus Enter, Tab, and Backspace. A BLE HID keyboard sends physical key usages,
not Unicode text. For Chinese or other input methods, select the matching iOS
keyboard and use `/v1/keyboard/key` or `/v1/keyboard/report` with the required
HID usages.

## Safety and behavior

- No listener is created unless `IPHONE_MIRROR_CONTROL_TOKEN` is present.
- The token is never written to the application log.
- The listener accepts loopback traffic only and limits request/header sizes.
- Commands are serialized so key and click press/release pairs cannot overlap.
- `GET /v1/screenshot` returns the latest mirrored frame as an authenticated PNG.
- API mode forwards physical input only while it is over/focused on the phone
  preview; it does not globally hide, clip, or capture the Windows mouse and
  keyboard.
- Stopping Bluetooth reverse control makes command endpoints return HTTP 409.
- Closing iPhoneMirror stops the listener before Bluetooth HID is disposed.
