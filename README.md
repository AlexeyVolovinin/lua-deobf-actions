# Deobfuscate Lua Scripts via GitHub Actions

Supported obfuscators:
- **Luraph** (C# + Java)
- **Prometheus** (JS — White Deobfuscator)
- **MoonSec V2/V3** (JS + .NET)
- **IronBrew2 / 25ms** (C# + JS)
- **AztupBrew** (JS)
- **Generic Lua deobfuscator** (Python)

## Usage

1. Push this repo to GitHub
2. Go to Actions → "Deobfuscate Lua Script" → Run workflow
3. Paste base64-encoded script or modify input.lua
4. Get results as artifact + Discord notification
