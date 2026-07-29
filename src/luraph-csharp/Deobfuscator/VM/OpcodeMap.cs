using System.Text.Json.Serialization;

namespace LuraphDeobfuscator.Deobfuscator.VM;
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpSrc
{
    None, RegA, RegB, RegC, OpA, OpB, OpC, UpvalA, UpvalB, UpvalC, JmpA, JmpB, JmpC, Nil,
}
public class OpcodeEntry
{
    public string Type { get; set; } = "";
    public string? Kind { get; set; }
    public OpSrc Dst { get; set; } = OpSrc.None;
    public OpSrc Src1 { get; set; } = OpSrc.None;
    public OpSrc Src2 { get; set; } = OpSrc.None;
    public OpSrc Target { get; set; } = OpSrc.None;
    public int? FixedArgs { get; set; }
    public int? FixedRets { get; set; }
    public char? ArgSlot { get; set; }
    public char? RetSlot { get; set; }
    public bool Invert { get; set; }
    public bool? TestCondition { get; set; }
    public string? Comment { get; set; }
}
public class FragmentEntry
{
    public string? G { get; set; }
    public string? F { get; set; }
    public string? Q { get; set; }
    public string? O { get; set; }
    public string? V { get; set; }
    public bool Commit { get; set; }
}
