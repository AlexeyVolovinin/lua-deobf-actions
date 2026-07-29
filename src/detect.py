import sys, re

SIGNATURES = [
    ("MoonSec V3", r'This file was protected with MoonSec V3'),
    ("MoonSec V2", r'\.\.:::MoonSec::\.\.'),
    ("IronBrew2 / 25ms", r'ironbrew|\[\[25ms\]\]|gCtfkH'),
    ("Luraph", r'Luraph\s*(Obfuscator|v\d+)'),
    ("Prometheus", r'prometheus|LPH!|PrometheusBytecodeMagic'),
    ("AztupBrew", r'AztupBrew'),
    ("LuaObfuscator", r'luaobfuscator|LuaObfuscator'),
    ("Moonsec (generic)", r'MOONSEC|_MOONSEC_'),
]

def detect(filename):
    with open(filename, 'r', errors='ignore') as f:
        content = f.read()
    
    print(f"File size: {len(content)} bytes")
    print(f"Lines: {content.count(chr(10))}")
    print()
    
    found = []
    for name, pattern in SIGNATURES:
        matches = re.findall(pattern, content, re.IGNORECASE)
        if matches:
            found.append(name)
            print(f"[DETECTED] {name}")
    
    if not found:
        print("[UNKNOWN] No known obfuscator detected")
    
    print()
    if found:
        print(f"Recommendation: Try {', '.join(found)} tools")
    else:
        print("Recommendation: Try generic deobfuscator or manual analysis")

if __name__ == '__main__':
    detect(sys.argv[1] if len(sys.argv) > 1 else 'input.lua')
