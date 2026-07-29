import re, sys

COMPOUND_ASSIGNMENT_OPERATORS = ("+=", "-=", "*=", "/=", "%=", "..=")

def deobfuscate(content):
    output = content
    
    output = re.sub(r'--\[\[.*?\]\]', '', output, flags=re.DOTALL)
    output = re.sub(r'--[^\n]*', '', output)
    
    output = re.sub(r'\(\(([^)]+)\)\)', r'\1', output)
    
    for op in COMPOUND_ASSIGNMENT_OPERATORS:
        pattern = re.compile(r'(\w+)\s*' + re.escape(op) + r'\s*([^;\n]+)')
        replacement = {
            "+=": r'\1 = \1 + \2',
            "-=": r'\1 = \1 - \2',
            "*=": r'\1 = \1 * \2',
            "/=": r'\1 = \1 / \2',
            "%=": r'\1 = \1 % \2',
            "..=": r'\1 = \1 .. \2',
        }
        output = pattern.sub(replacement[op], output)
    
    output = re.sub(r'\n\s*\n', '\n', output)
    output = output.strip()
    
    return output

if __name__ == '__main__':
    if len(sys.argv) < 3:
        print("Usage: python deobfuscator.py input.lua output.lua")
        sys.exit(1)
    
    with open(sys.argv[1], 'r', errors='ignore') as f:
        content = f.read()
    
    result = deobfuscate(content)
    
    with open(sys.argv[2], 'w') as f:
        f.write(result)
    
    print(f"Deobfuscated {len(content)} -> {len(result)} chars")
    print(f"Saved to {sys.argv[2]}")
