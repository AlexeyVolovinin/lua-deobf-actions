namespace LuraphDeobfuscator.Deobfuscator.VM;
public class LuraphProto
{
    public int InstructionCount { get; set; }
    public int[] Opcodes { get; set; } = [];
    public int[] RawA { get; set; } = [];
    public int[] RawB { get; set; } = [];
    public int[] RawC { get; set; } = [];
    public int[] ModeA { get; set; } = [];
    public int[] ModeB { get; set; } = [];
    public int[] ModeC { get; set; } = [];
    public int[] RegA { get; set; } = [];
    public int[] RegB { get; set; } = [];
    public int[] RegC { get; set; } = [];
    public object?[] ConstsB { get; set; } = [];
    public object?[] ConstsA { get; set; } = [];
    public object?[] ProtoRefs { get; set; } = [];
    public int StackSize { get; set; }
    public int NumParams { get; set; }
    public int UpvalueCount { get; set; }
    public List<UpvalueDesc> Upvalues { get; set; } = [];
    public Dictionary<int, int> LineInfo { get; set; } = [];
    public int HandlerIndex { get; set; } = -1;
    public List<LuraphProto> Children { get; set; } = [];
    public List<(int InstrIndex, int ProtoIndex)> ClosureBackpatches { get; set; } = [];
    public string Name { get; set; } = "";
}

public class UpvalueDesc
{
    public int InStack { get; set; }
    public int Register { get; set; }
}
