using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuraphDeobfuscator.Deobfuscator.VM;
public class LuraphConstant
{
    public LuraphConstantType Type { get; set; }
    public object? Value { get; set; }
    public byte[]? RawBytes { get; set; }

    public override string ToString() => Type switch
    {
        LuraphConstantType.Nil => "nil",
        LuraphConstantType.Boolean => (bool)Value! ? "true" : "false",
        LuraphConstantType.Number => Value!.ToString()!,
        LuraphConstantType.Integer => Value!.ToString()!,
        LuraphConstantType.String when Value is string s => $"\"{s}\"",
        _ => $"<{Type}>"
    };
}

public enum LuraphConstantType
{
    Nil, Boolean, Number, Integer, String,
}
public class LuraphSettings
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int ConstantsOffset { get; set; } = 231;
    public int PrototypesOffset { get; set; } = 14954;
    public int InstructionsOffset { get; set; } = 58516;
    public int DispatchMode { get; set; } = 1;
    public int? FloatTag { get; set; } = 87;
    public int? StringTag { get; set; } = 216;
    public int? IntegerTag { get; set; }
    public int ProtoFormat { get; set; } = 1;
    public bool TwoChunk { get; set; }
    public int BootstrapSkipBytes { get; set; }
    public Dictionary<int, OperandSemantic> OperandModes { get; set; } = new()
    {
        [0] = OperandSemantic.Constant,
        [1] = OperandSemantic.RelForward,
        [2] = OperandSemantic.ClosureRef,
        [4] = OperandSemantic.RelBackward,
        [7] = OperandSemantic.Register,
    };
    public bool DecryptStrings { get; set; }
    public int CharTableInitState { get; set; } = 0;
    public int CharTableXorMask { get; set; } = 127;
    public int LcgMultiplier { get; set; } = 65;
    public int LcgIncrement { get; set; } = 117;
    public bool ApplyConstantTransforms { get; set; }
    public bool BooleanAsVarInt { get; set; }
    public Dictionary<int, OpcodeEntry>? OpcodeMap { get; set; }
    public Dictionary<int, FragmentEntry>? FragmentMap { get; set; }
    public List<int>? NopOpcodes { get; set; }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    public void SaveToJson(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, _jsonOpts);
        File.WriteAllText(path, json);
    }
    public static LuraphSettings LoadFromJson(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LuraphSettings>(json, _jsonOpts)
            ?? throw new InvalidOperationException($"Failed to deserialize config from {path}");
    }
    public string ToJson() => JsonSerializer.Serialize(this, _jsonOpts);
}

public enum OperandSemantic
{
    Register, Constant, ClosureRef, RelForward, RelBackward, ResolvedConst, ProtoIndex,
}
public class LuraphChunk
{
    public List<LuraphConstant> Constants { get; set; } = [];
    public List<LuraphProto> Prototypes { get; set; } = [];
    public int EntryIndex { get; set; }
    public LuraphProto? EntryProto { get; set; }
    public bool IsCached { get; set; }
    public LuraphSettings Settings { get; set; } = new();
    public LuraphChunk? AntiTamperChunk { get; set; }
}
public class LuraphDecoder
{
    private byte[] _data = [];
    private int _pos;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private const int MaxVarIntBytes = 6;
    private const int MaxAutoDeserializeAttempts = 10000;
    private static readonly TimeSpan MaxAutoDeserializeTime = TimeSpan.FromSeconds(8);
    private const int MinAutoDeserializeScore = 220;
    private const int MinAutoDeserializeScoreFallback = 140;

    public static byte[] DecodeBase85(string encoded)
    {
        if (encoded.StartsWith("LPH") && encoded.Length > 4)
            encoded = encoded[4..];
        encoded = encoded.Replace("z", "!!!!!");

        var result = new List<byte>();
        int i = 0;
        while (i + 4 < encoded.Length)
        {
            int a = encoded[i] - 33;
            int b = encoded[i + 1] - 33;
            int c = encoded[i + 2] - 33;
            int d = encoded[i + 3] - 33;
            int e = encoded[i + 4] - 33;

            long value = (long)a * 52200625 + (long)b * 614125 + (long)c * 7225 + (long)d * 85 + e;

            result.Add((byte)((value >> 24) & 0xFF));
            result.Add((byte)((value >> 16) & 0xFF));
            result.Add((byte)((value >> 8) & 0xFF));
            result.Add((byte)(value & 0xFF));

            i += 5;
        }

        return result.ToArray();
    }

    private byte ReadByte()
    {
        if (_pos >= _data.Length)
            throw new EndOfStreamException($"Unexpected end of blob at byte offset {_pos}.");
        return _data[_pos++];
    }

    private int RemainingBytes() => _data.Length - _pos;

    private uint ReadUInt32()
    {
        uint a = ReadByte();
        uint b = ReadByte();
        uint c = ReadByte();
        uint d = ReadByte();
        return d * 16777216u + c * 65536u + b * 256u + a;
    }

    private int ReadVarInt()
    {
        long value = 0;
        long multiplier = 1;
        for (int i = 0; i < MaxVarIntBytes; i++)
        {
            byte b = ReadByte();
            int data = b > 127 ? b - 128 : b;

            value += data * multiplier;
            if (value > int.MaxValue)
                throw new Exception("VarInt value overflow.");

            if (b < 128)
                return (int)value;

            multiplier *= 128;
        }

        throw new Exception($"VarInt exceeded maximum length ({MaxVarIntBytes} bytes).");
    }

    private long ReadInt64()
    {
        uint low = ReadUInt32();
        uint high = ReadUInt32();
        long highSigned = high >= 2147483648u ? (long)high - 4294967296L : high;
        return highSigned * 4294967296L + low;
    }

    private double ReadFloat64()
    {
        uint left = ReadUInt32();
        uint right = ReadUInt32();

        if (left == 0 && right == 0) return 0.0;

        int sign = (int)((right >> 31) & 1);
        int exponent = (int)((right >> 20) & 0x7FF);
        long mantissa = ((long)(right & 0xFFFFF)) * 4294967296L + left;
        int isNormal = 1;

        if (exponent == 0)
        {
            if (mantissa == 0) return sign == 1 ? -0.0 : 0.0;
            isNormal = 0;
            exponent = 1;
        }
        else if (exponent == 2047)
        {
            return mantissa == 0
                ? (sign == 1 ? double.NegativeInfinity : double.PositiveInfinity)
                : double.NaN;
        }

        double s = sign == 1 ? -1.0 : 1.0;
        return s * Math.Pow(2, exponent - 1023) * (mantissa / 4503599627370496.0 + isNormal);
    }

    private byte[] ReadRawBytes()
    {
        int len = ReadVarInt();
        if (len < 0)
            throw new Exception($"Negative byte-string length: {len}");
        if (len == 0) return [];

        if (len > RemainingBytes())
            throw new EndOfStreamException($"String length {len} exceeds remaining blob bytes {RemainingBytes()}.");

        var bytes = new byte[len];
        Buffer.BlockCopy(_data, _pos, bytes, 0, len);
        _pos += len;
        return bytes;
    }

    private string ReadString()
    {
        var bytes = ReadRawBytes();
        if (bytes.Length == 0) return "";
        return Encoding.UTF8.GetString(bytes);
    }
    private static string DecryptString(byte[] raw, LuraphSettings settings)
    {
        if (raw.Length == 0) return "";

        byte flag = raw[0];
        if (flag == 0)
        {
            var plain = new byte[raw.Length - 1];
            Buffer.BlockCopy(raw, 1, plain, 0, plain.Length);
            return DecodeBytesSmart(plain);
        }

        if (raw.Length < 3)
        {
            return DecodeBytesSmart(raw);
        }

        int a = settings.LcgMultiplier;
        int c = settings.LcgIncrement;

        var candidates = new List<string>(8);
        candidates.Add(DecodeBytesSmart(DecryptStringBytes(raw, settings.CharTableInitState, settings.CharTableXorMask, a, c, true, true)));
        candidates.Add(DecodeBytesSmart(DecryptStringBytes(raw, settings.CharTableInitState, settings.CharTableXorMask, a, c, true, false)));
        if (settings.CharTableInitState != 0)
            candidates.Add(DecodeBytesSmart(DecryptStringBytes(raw, 0, settings.CharTableXorMask, a, c, true, true)));
        if (settings.CharTableInitState != 185)
            candidates.Add(DecodeBytesSmart(DecryptStringBytes(raw, 185, settings.CharTableXorMask, a, c, true, true)));
        candidates.Add(DecodeBytesSmart(DecryptStringBytes(raw, settings.CharTableInitState, settings.CharTableXorMask, a, c, false, true)));

        return candidates
            .OrderByDescending(ScoreDecodedString)
            .FirstOrDefault() ?? "";
    }

    private static byte[] DecryptStringBytes(byte[] raw, int charTableInitState, int charTableXorMask, int a, int c, bool useCharTable, bool advanceKeyBeforeDecrypt)
    {
        var charTable = useCharTable
            ? BuildCharTable(charTableInitState, charTableXorMask, a, c)
            : null;

        int key = raw[1];
        if (advanceKeyBeforeDecrypt)
            key = (a * key + c) % 256;

        var plain = new byte[raw.Length - 2];
        for (int i = 2; i < raw.Length; i++)
        {
            int idx = (raw[i] ^ key) & 0xFF;
            plain[i - 2] = charTable != null ? charTable[idx] : (byte)idx;
            key = (a * key + c) % 256;
        }

        return plain;
    }
    private static byte[] BuildCharTable(LuraphSettings settings)
        => BuildCharTable(settings.CharTableInitState, settings.CharTableXorMask, settings.LcgMultiplier, settings.LcgIncrement);

    private static byte[] BuildCharTable(int charTableInitState, int charTableXorMask, int a, int c)
    {
        var table = new byte[256];
        int state = charTableInitState;
        int xorMask = charTableXorMask;

        for (int i = 0; i < 256; i++)
        {
            int index = (state ^ xorMask) & 0xFF;
            table[index] = (byte)state;
            state = (a * state + c) % 256;
        }

        return table;
    }

    private static string DecodeBytesSmart(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        try
        {
            return _strictUtf8.GetString(bytes);
        }
        catch
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static int ScoreDecodedString(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;

        int score = 0;
        int printable = 0;

        foreach (char ch in s)
        {
            if (ch == '\uFFFD') score -= 10;

            if (ch == '\n' || ch == '\r' || ch == '\t' || (ch >= 32 && ch <= 126))
            {
                printable++;
                score += 2;
            }
            else if (ch < 32)
            {
                score -= 2;
            }
            else
            {
                score += 1;
            }
        }
        if (printable * 100 / Math.Max(1, s.Length) >= 70)
            score += 15;

        return score;
    }

    private void ValidateCountFitsRemaining(string label, int count, int minBytesPerItem)
    {
        if (count < 0)
            throw new Exception($"{label} cannot be negative: {count}");
        if (minBytesPerItem < 1)
            minBytesPerItem = 1;

        long minBytes = (long)count * minBytesPerItem;
        if (minBytes > RemainingBytes())
            throw new Exception($"{label} too large for remaining data: {count} items need at least {minBytes} bytes, only {RemainingBytes()} left.");
    }

    private int ResolveOffsetCount(string label, int rawCount, int primaryOffset, int fallbackCountLimit, int minBytesPerItem, params int[] fallbackOffsets)
    {
        var seen = new HashSet<int>();
        int[] candidates = [primaryOffset, ..fallbackOffsets];
        foreach (var offset in candidates)
        {
            if (!seen.Add(offset))
                continue;
            int count = rawCount - offset;
            if (count < 0 || count > fallbackCountLimit)
                continue;

            try
            {
                ValidateCountFitsRemaining(label, count, minBytesPerItem);
            }
            catch
            {
                continue;
            }

            return count;
        }

        if (rawCount >= 0 && rawCount <= fallbackCountLimit && !seen.Contains(0))
        {
            try
            {
                ValidateCountFitsRemaining(label, rawCount, minBytesPerItem);
            }
            catch
            {
                return -1;
            }

            return rawCount;
        }

        return -1;
    }

    private (List<LuraphConstant> constants, bool isCached) DeserializeConstants(LuraphSettings settings)
    {
        int rawCount = ReadVarInt();
        int count = ResolveOffsetCount("Constant count", rawCount, settings.ConstantsOffset, 500000, 1,
            settings.InstructionsOffset, settings.PrototypesOffset);

        if (count < 0)
            throw new Exception($"Negative constant count: {rawCount - settings.ConstantsOffset} (raw={rawCount}, offset={settings.ConstantsOffset})");

        bool isCached = ReadByte() != 0;

        Func<int, string> classify = settings.DispatchMode switch
        {
            1 => tag =>
            {
                if (tag == settings.FloatTag) return "number";
                if (tag < settings.FloatTag) return "boolean";
                if (tag == settings.StringTag) return "string";
                return "integer";
            }
            ,
            2 => tag =>
            {
                if (tag <= settings.StringTag)
                    return tag == settings.StringTag ? "string" : "number";
                return settings.IntegerTag.HasValue && tag == settings.IntegerTag.Value ? "integer" : "boolean";
            }
            ,
            _ => tag =>
            {
                if (tag == settings.StringTag) return "string";
                if (settings.FloatTag.HasValue && tag == settings.FloatTag.Value) return "number";
                if (settings.IntegerTag.HasValue && tag == settings.IntegerTag.Value) return "integer";
                return "boolean";
            }
        };

        var constants = new List<LuraphConstant>(count);
        for (int i = 0; i < count; i++)
        {
            int typeTag = ReadByte();
            string dataType = classify(typeTag);

            var entry = new LuraphConstant();
            switch (dataType)
            {
                case "number":
                    entry.Type = LuraphConstantType.Number;
                    double num = ReadFloat64();
                    if (settings.ApplyConstantTransforms && num != 0)
                        num = -num;
                    entry.Value = num;
                    break;
                case "boolean":
                    entry.Type = LuraphConstantType.Boolean;
                    int rawBool = settings.BooleanAsVarInt ? ReadVarInt() : ReadByte();
                    bool bval = rawBool != 0;
                    if (settings.ApplyConstantTransforms)
                        bval = !bval;
                    entry.Value = bval;
                    break;
                case "string":
                    entry.Type = LuraphConstantType.String;
                    var rawBytes = ReadRawBytes();
                    entry.RawBytes = rawBytes;
                    if (settings.DecryptStrings)
                    {
                        entry.Value = DecryptString(rawBytes, settings);
                    }
                    else
                    {
                        entry.Value = rawBytes.Length > 0
                            ? DecodeBytesSmart(rawBytes)
                            : "";
                    }
                    break;
                case "integer":
                    entry.Type = LuraphConstantType.Integer;
                    entry.Value = ReadInt64();
                    break;
            }
            constants.Add(entry);
        }

        return (constants, isCached);
    }

    private LuraphProto DeserializePrototype(LuraphSettings settings, List<LuraphConstant> constants, bool isCached)
    {
        return settings.ProtoFormat switch
        {
            2 => DeserializeProtoV2(settings, constants, isCached),
            3 => DeserializeProtoV3(settings, constants, isCached),
            _ => DeserializeProtoV1(settings, constants, isCached),
        };
    }
    private LuraphProto DeserializeProtoV1(LuraphSettings settings, List<LuraphConstant> constants, bool isCached)
    {
        var proto = new LuraphProto();
        int metadataCount = ReadVarInt();
        if (metadataCount > 100000)
            throw new Exception($"Metadata count too large: {metadataCount}");
        ValidateCountFitsRemaining("Metadata count", metadataCount, 1);
        for (int i = 0; i < metadataCount; i++) ReadVarInt();
        int rawInstrCount = ReadVarInt();
        int instrCount = ResolveOffsetCount("Instruction count", rawInstrCount, settings.InstructionsOffset, 1000000, 4,
            settings.ConstantsOffset, settings.PrototypesOffset);
        if (instrCount < 0) throw new Exception($"Negative instruction count: {rawInstrCount - settings.InstructionsOffset} (raw={rawInstrCount}, offset={settings.InstructionsOffset})");
        proto.InstructionCount = instrCount;
        ReadInstructions(proto, instrCount, constants, isCached, settings, "CABO");
        proto.StackSize = ReadVarInt();
        int lineEntryCount = (int)ReadUInt32();
        if (lineEntryCount < 0 || lineEntryCount > 1000000)
            throw new Exception($"Invalid line info count: {lineEntryCount}");
        ValidateCountFitsRemaining("Line info count", lineEntryCount, 4);
        for (int i = 0; i < lineEntryCount; i++)
        {
            uint raw = ReadUInt32();
            int halfVal = (int)(raw / 2);
            if (raw % 2 == 0)
            {
                proto.LineInfo[i + 1] = halfVal;
            }
            else
            {
                int rangeEnd = (int)ReadUInt32();
                int lineNumber = (int)ReadUInt32();
                for (int j = halfVal; j <= rangeEnd; j++)
                    proto.LineInfo[j] = lineNumber;
            }
        }
        proto.NumParams = ReadVarInt();

        return proto;
    }
    private LuraphProto DeserializeProtoV2(LuraphSettings settings, List<LuraphConstant> constants, bool isCached)
    {
        var proto = new LuraphProto();

        proto.StackSize = ReadVarInt();

        int rawInstrCount = ReadVarInt();
        int instrCount = ResolveOffsetCount("Instruction count", rawInstrCount, settings.InstructionsOffset, 1000000, 4,
            settings.ConstantsOffset, settings.PrototypesOffset);
        if (instrCount < 0) throw new Exception($"Negative instruction count: {rawInstrCount - settings.InstructionsOffset} (raw={rawInstrCount}, offset={settings.InstructionsOffset})");
        proto.InstructionCount = instrCount;

        ReadInstructions(proto, instrCount, constants, isCached, settings, "OpABC");

        int rawNumParams = ReadVarInt();
        proto.NumParams = rawNumParams;

        int upvalueCount = ReadVarInt();
        if (upvalueCount < 0 || upvalueCount > 100000)
            throw new Exception($"Invalid upvalue count: {upvalueCount}");
        ValidateCountFitsRemaining("Upvalue count", upvalueCount, 1);
        proto.UpvalueCount = upvalueCount;
        for (int i = 0; i < upvalueCount; i++)
        {
            int raw = ReadVarInt();
            proto.Upvalues.Add(new UpvalueDesc { InStack = raw % 4, Register = raw / 4 });
        }

        return proto;
    }
    private LuraphProto DeserializeProtoV3(LuraphSettings settings, List<LuraphConstant> constants, bool isCached)
    {
        var proto = new LuraphProto();

        int rawInstrCount = ReadVarInt();
        int instrCount = ResolveOffsetCount("Instruction count", rawInstrCount, settings.InstructionsOffset, 1000000, 4,
            settings.ConstantsOffset, settings.PrototypesOffset);
        if (instrCount < 0) throw new Exception($"Negative instruction count: {rawInstrCount - settings.InstructionsOffset} (raw={rawInstrCount}, offset={settings.InstructionsOffset})");
        proto.InstructionCount = instrCount;

        ReadInstructions(proto, instrCount, constants, isCached, settings, "ABOpC");

        proto.StackSize = ReadVarInt();
        proto.HandlerIndex = ReadVarInt();

        int upvalueCount = ReadVarInt();
        if (upvalueCount < 0 || upvalueCount > 100000)
            throw new Exception($"Invalid upvalue count: {upvalueCount}");
        ValidateCountFitsRemaining("Upvalue count", upvalueCount, 1);
        proto.UpvalueCount = upvalueCount;
        for (int i = 0; i < upvalueCount; i++)
        {
            int raw = ReadVarInt();
            proto.Upvalues.Add(new UpvalueDesc { InStack = raw % 4, Register = raw / 4 });
        }

        return proto;
    }
    private void ReadInstructions(LuraphProto proto, int instrCount, List<LuraphConstant> constants, bool isCached, LuraphSettings settings, string readOrder)
    {
        proto.Opcodes = new int[instrCount + 1];
        proto.RawA = new int[instrCount + 1];
        proto.RawB = new int[instrCount + 1];
        proto.RawC = new int[instrCount + 1];
        proto.ModeA = new int[instrCount + 1];
        proto.ModeB = new int[instrCount + 1];
        proto.ModeC = new int[instrCount + 1];
        proto.RegA = new int[instrCount + 1];
        proto.RegB = new int[instrCount + 1];
        proto.RegC = new int[instrCount + 1];
        proto.ConstsA = new object?[instrCount + 1];
        proto.ConstsB = new object?[instrCount + 1];
        proto.ProtoRefs = new object?[instrCount + 1];

        var closureBackpatches = new List<(int instrIndex, int protoIndex)>();

        for (int idx = 1; idx <= instrCount; idx++)
        {
            int opcode, rawA, rawB, rawC;

            switch (readOrder)
            {
                case "OpABC":
                    opcode = ReadVarInt(); rawA = ReadVarInt(); rawB = ReadVarInt(); rawC = ReadVarInt();
                    break;
                case "ABOpC":
                    rawA = ReadVarInt(); rawB = ReadVarInt(); opcode = ReadVarInt(); rawC = ReadVarInt();
                    break;
                default:
                    rawC = ReadVarInt(); rawA = ReadVarInt(); rawB = ReadVarInt(); opcode = ReadVarInt();
                    break;
            }

            int modeA = rawA % 8, valA = rawA / 8;
            int modeB = rawB % 8, valB = rawB / 8;
            int modeC = rawC % 8, valC = rawC / 8;

            proto.Opcodes[idx] = opcode;
            proto.ModeA[idx] = modeA;
            proto.ModeB[idx] = modeB;
            proto.ModeC[idx] = modeC;
            proto.RawA[idx] = valA;
            proto.RawB[idx] = valB;
            proto.RawC[idx] = valC;
            proto.RegA[idx] = valA;
            proto.RegB[idx] = valB;
            proto.RegC[idx] = valC;
            ApplyOperandMode(proto, idx, 'B', modeB, valB, constants, isCached, settings, closureBackpatches);
            ApplyOperandMode(proto, idx, 'C', modeC, valC, constants, isCached, settings, closureBackpatches);
            ApplyOperandMode(proto, idx, 'A', modeA, valA, constants, isCached, settings, closureBackpatches);
        }
        proto.ClosureBackpatches = closureBackpatches;
        proto.Children = [];
    }

    private void ApplyOperandMode(LuraphProto proto, int idx, char slot, int mode, int val, List<LuraphConstant> constants, bool isCached, LuraphSettings settings, List<(int instrIndex, int protoIndex)> closureBackpatches)
    {
        if (!settings.OperandModes.TryGetValue(mode, out var semantic))
            return;

        switch (slot)
        {
            case 'B':
                switch (semantic)
                {
                    case OperandSemantic.Constant:
                    case OperandSemantic.ResolvedConst:
                        if (val > 0 && val <= constants.Count)
                            proto.ConstsB[idx] = constants[val - 1].Value;
                        else if (val >= 0 && val < constants.Count)
                            proto.ConstsB[idx] = constants[val].Value;
                        else
                            proto.ConstsB[idx] = val;
                        break;
                    case OperandSemantic.RelForward:
                        proto.RegB[idx] = idx + val + 1;
                        break;
                    case OperandSemantic.RelBackward:
                        proto.RegB[idx] = idx - val + 1;
                        break;
                    case OperandSemantic.ProtoIndex:
                    case OperandSemantic.ClosureRef:
                        closureBackpatches.Add((idx, val));
                        break;
                }
                break;

            case 'C':
                switch (semantic)
                {
                    case OperandSemantic.Constant:
                    case OperandSemantic.ResolvedConst:
                        if (val > 0 && val <= constants.Count)
                            proto.ProtoRefs[idx] = constants[val - 1].Value;
                        else if (val >= 0 && val < constants.Count)
                            proto.ProtoRefs[idx] = constants[val].Value;
                        else
                            proto.ProtoRefs[idx] = val;
                        break;
                    case OperandSemantic.RelForward:
                        proto.RegC[idx] = idx + val + 1;
                        break;
                    case OperandSemantic.RelBackward:
                        proto.RegC[idx] = idx - val + 1;
                        break;
                    case OperandSemantic.ClosureRef:
                    case OperandSemantic.ProtoIndex:
                        closureBackpatches.Add((idx, val));
                        break;
                }
                break;

            case 'A':
                switch (semantic)
                {
                    case OperandSemantic.Constant:
                    case OperandSemantic.ResolvedConst:
                        if (val > 0 && val <= constants.Count)
                            proto.ConstsA[idx] = constants[val - 1].Value;
                        else if (val >= 0 && val < constants.Count)
                            proto.ConstsA[idx] = constants[val].Value;
                        else
                            proto.ConstsA[idx] = val;
                        break;
                    case OperandSemantic.RelForward:
                        proto.RegA[idx] = idx + val + 1;
                        break;
                    case OperandSemantic.RelBackward:
                        proto.RegA[idx] = idx - val + 1;
                        break;
                    case OperandSemantic.ProtoIndex:
                    case OperandSemantic.ClosureRef:
                        closureBackpatches.Add((idx, val));
                        break;
                }
                break;
        }
    }

    private LuraphChunk DeserializeChunk(LuraphSettings settings)
    {
        var (constants, isCached) = DeserializeConstants(settings);

        int rawProtoCount = ReadVarInt();
        int protoCount = ResolveOffsetCount("Prototype count", rawProtoCount, settings.PrototypesOffset, 100000, 1,
            settings.ConstantsOffset, settings.InstructionsOffset);
        if (protoCount < 0)
            throw new Exception($"Negative proto count: {rawProtoCount - settings.PrototypesOffset} (raw={rawProtoCount}, offset={settings.PrototypesOffset})");

        var prototypes = new List<LuraphProto>(protoCount);
        for (int i = 0; i < protoCount; i++)
        {
            var proto = DeserializePrototype(settings, constants, isCached);
            proto.Name = $"P{i + 1}";
            prototypes.Add(proto);
        }
        foreach (var proto in prototypes)
        {
            foreach (var (instrIdx, protoIdx) in proto.ClosureBackpatches)
            {
                if (protoIdx >= 1 && protoIdx <= prototypes.Count)
                {
                    proto.RegC[instrIdx] = protoIdx - 1;
                    proto.Children.Add(prototypes[protoIdx - 1]);
                }
                else if (protoIdx >= 0 && protoIdx < prototypes.Count)
                {
                    proto.RegC[instrIdx] = protoIdx;
                    proto.Children.Add(prototypes[protoIdx]);
                }
            }
        }
        int rawEntryIndex = ReadVarInt();
        int entryIndex = rawEntryIndex - 1;

        var chunk = new LuraphChunk
        {
            Constants = constants,
            Prototypes = prototypes,
            EntryIndex = entryIndex,
            EntryProto = entryIndex >= 0 && entryIndex < prototypes.Count ? prototypes[entryIndex] : null,
            IsCached = isCached,
            Settings = settings,
        };

        return chunk;
    }
    public LuraphChunk Deserialize(string blob, LuraphSettings? settings = null)
    {
        settings ??= new LuraphSettings();
        _data = DecodeBase85(blob);
        if (_data.Length == 0)
            throw new Exception("Decoded blob is empty.");
        _pos = 0;

        if (settings.TwoChunk)
        {
            bool savedDecrypt = settings.DecryptStrings;
            bool savedTransform = settings.ApplyConstantTransforms;
            settings.DecryptStrings = false;
            settings.ApplyConstantTransforms = false;

            var chunk1 = DeserializeChunk(settings);
            settings.DecryptStrings = savedDecrypt;
            settings.ApplyConstantTransforms = savedTransform;
            for (int i = 0; i < settings.BootstrapSkipBytes; i++)
                ReadByte();
            var chunk2 = DeserializeChunk(settings);
            chunk2.AntiTamperChunk = chunk1;
            return chunk2;
        }

        return DeserializeChunk(settings);
    }
    public LuraphChunk? TryDeserialize(string blob, int[] candidates, LuraphSettings? baseSettings = null)
    {
        baseSettings ??= new LuraphSettings();

        var dispatchConfigs = new[]
        {
            (Mode: 1, Float: (int?)87,  String: (int?)216, Integer: (int?)null, Format: 1), (Mode: 2, Float: (int?)null, String: (int?)13,  Integer: (int?)199,  Format: 2), (Mode: 3, Float: (int?)240,  String: (int?)149, Integer: (int?)143,  Format: 3), };

        LuraphChunk? best = null;
        int bestScore = int.MinValue;

        foreach (var dc in dispatchConfigs)
        {
            for (int i = 0; i < candidates.Length; i++)
                for (int j = 0; j < candidates.Length; j++)
                {
                    if (j == i) continue;
                    for (int k = 0; k < candidates.Length; k++)
                    {
                        if (k == i || k == j) continue;

                        var s = new LuraphSettings
                        {
                            ConstantsOffset = candidates[i],
                            PrototypesOffset = candidates[j],
                            InstructionsOffset = candidates[k],
                            DispatchMode = dc.Mode,
                            FloatTag = dc.Float,
                            StringTag = dc.String,
                            IntegerTag = dc.Integer,
                            ProtoFormat = dc.Format,
                            TwoChunk = baseSettings.TwoChunk,
                            BootstrapSkipBytes = baseSettings.BootstrapSkipBytes,
                            OperandModes = baseSettings.OperandModes,
                        };

                        EnsureRuntimeDefaults(s);

                        try
                        {
                            _pos = 0;
                            _data = DecodeBase85(blob);
                            var result = DeserializeChunk(s);
                            int score = ScoreChunk(result);
                            if (score > bestScore)
                            {
                                best = result;
                                bestScore = score;
                                if (score >= 450)
                                    return best;
                            }
                        }
                        catch { }
                    }
                }
        }

        return best;
    }
    public (LuraphChunk? chunk, LuraphSettings? settings, string? error) AutoDeserialize(string blob, LuraphSettings? preferredSettings = null)
    {
        preferredSettings ??= new LuraphSettings();
        string? bestError = null;
        var searchTimer = Stopwatch.StartNew();
        int attempts = 0;
        var preferred = CloneSettings(preferredSettings);
        EnsureRuntimeDefaults(preferred);
        LuraphChunk? bestChunk = null;
        LuraphSettings? bestSettings = null;
        int bestScore = int.MinValue;
        try
        {
            var direct = Deserialize(blob, preferred);
            int directScore = ScoreChunk(direct);
            if (LooksPlausibleChunk(direct) && directScore >= MinAutoDeserializeScoreFallback)
                return (direct, preferred, null);

            bestChunk = direct;
            bestSettings = preferred;
            bestScore = directScore;
            bestError = $"Direct parse produced low-quality chunk (score {bestScore}).";
        }
        catch (Exception ex)
        {
            bestError = ex.Message;
        }

        byte[] data;
        try { data = DecodeBase85(blob); }
        catch (Exception ex) { return (null, null, ex.Message); }

        var allOffsetCandidates = BuildOffsetCandidates(data, preferredSettings);
        var profiles = BuildInferenceProfiles(preferredSettings);
        bool stopSearch = false;
        bool foundKnownCandidate = false;

        // Fast pass: treat inferred offsets as an unordered set and try role permutations on profile variants first.
        var seenQuickAttempts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quickProfile in profiles)
        {
            var quickBase = CloneSettings(quickProfile);
            EnsureRuntimeDefaults(quickBase);

            var quickOffsets = new[] { quickBase.ConstantsOffset, quickBase.PrototypesOffset, quickBase.InstructionsOffset };
            var twoChunkModes = quickBase.TwoChunk ? new[] { true, false } : new[] { false, true };
            var quickSkipCandidates = MergeCandidates([quickBase.BootstrapSkipBytes, 0, 101]);

        for (int c = 0; c < quickOffsets.Length; c++)
            for (int p = 0; p < quickOffsets.Length; p++)
            {
                if (p == c) continue;
                for (int i = 0; i < quickOffsets.Length; i++)
                {
                    if (i == c || i == p) continue;

                    foreach (var twoChunk in twoChunkModes)
                    {
                        var skipCandidates = twoChunk ? quickSkipCandidates : [0];
                        foreach (var skip in skipCandidates)
                        {
                            foreach (var boolAsVarInt in new[] { false, true })
                            {
                                if (attempts >= MaxAutoDeserializeAttempts || searchTimer.Elapsed >= MaxAutoDeserializeTime)
                                {
                                    stopSearch = true;
                                    break;
                                }

                                var attempt = CloneSettings(quickBase);
                                attempt.ConstantsOffset = quickOffsets[c];
                                attempt.PrototypesOffset = quickOffsets[p];
                                attempt.InstructionsOffset = quickOffsets[i];
                                attempt.TwoChunk = twoChunk;
                                attempt.BootstrapSkipBytes = skip;
                                attempt.BooleanAsVarInt = boolAsVarInt;
                                EnsureRuntimeDefaults(attempt);

                                string attemptKey = $"{attempt.DispatchMode}:{attempt.ProtoFormat}:{attempt.FloatTag?.ToString() ?? "n"}:{attempt.StringTag?.ToString() ?? "n"}:{attempt.IntegerTag?.ToString() ?? "n"}:{attempt.ConstantsOffset}:{attempt.PrototypesOffset}:{attempt.InstructionsOffset}:{attempt.TwoChunk}:{attempt.BootstrapSkipBytes}:{attempt.BooleanAsVarInt}";
                                if (!seenQuickAttempts.Add(attemptKey))
                                    continue;

                                try
                                {
                                    attempts++;
                                    var chunk = Deserialize(blob, attempt);
                                    int score = ScoreChunk(chunk);
                                    if (score > bestScore)
                                    {
                                        bestScore = score;
                                        bestChunk = chunk;
                                        bestSettings = attempt;
                                        if (attempt.DispatchMode == 3 && attempt.ProtoFormat == 3)
                                            foundKnownCandidate = true;
                                        if (score >= 450)
                                            return (bestChunk, bestSettings, null);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    bestError ??= ex.Message;
                                }
                            }
                        }

                        if (stopSearch) break;
                    }

                        if (stopSearch) break;
                    }

                    if (stopSearch) break;
                }

            if (stopSearch) break;
        }

        foreach (var profile in profiles)
        {
            var roleHints = new[]
            {
                profile.ConstantsOffset, profile.PrototypesOffset, profile.InstructionsOffset,
                preferred.ConstantsOffset, preferred.PrototypesOffset, preferred.InstructionsOffset,
            };

            var constOffsets = MergeCandidates(
                PickClosestCandidates(allOffsetCandidates, profile.ConstantsOffset, 8),
                PickClosestCandidates(allOffsetCandidates, preferred.ConstantsOffset, 8),
                roleHints,
                [231, 25087, profile.ConstantsOffset, preferred.ConstantsOffset]);

            var protoOffsets = MergeCandidates(
                PickClosestCandidates(allOffsetCandidates, profile.PrototypesOffset, 8),
                PickClosestCandidates(allOffsetCandidates, preferred.PrototypesOffset, 8),
                roleHints,
                [14954, 80260, profile.PrototypesOffset, preferred.PrototypesOffset]);

            var instrOffsets = MergeCandidates(
                PickClosestCandidates(allOffsetCandidates, profile.InstructionsOffset, 8),
                PickClosestCandidates(allOffsetCandidates, preferred.InstructionsOffset, 8),
                roleHints,
                [58516, 94134, profile.InstructionsOffset, preferred.InstructionsOffset]);

            var skipCandidates = profile.TwoChunk
                ? BuildBootstrapCandidates(profile.BootstrapSkipBytes)
                : [0];

            foreach (var skip in skipCandidates)
            {
                foreach (var cOff in constOffsets)
                    foreach (var pOff in protoOffsets)
                    {
                        if (pOff == cOff) continue;
                        foreach (var iOff in instrOffsets)
                        {
                            if (attempts >= MaxAutoDeserializeAttempts || searchTimer.Elapsed >= MaxAutoDeserializeTime)
                            {
                                stopSearch = true;
                                break;
                            }

                            if (iOff == cOff || iOff == pOff) continue;

                            var attempt = CloneSettings(profile);
                            attempt.ConstantsOffset = cOff;
                            attempt.PrototypesOffset = pOff;
                            attempt.InstructionsOffset = iOff;
                            attempt.BootstrapSkipBytes = skip;
                            foreach (var boolAsVarInt in new[] { false, true })
                            {
                                if (attempts >= MaxAutoDeserializeAttempts || searchTimer.Elapsed >= MaxAutoDeserializeTime)
                                {
                                    stopSearch = true;
                                    break;
                                }

                                attempt.BooleanAsVarInt = boolAsVarInt;
                                EnsureRuntimeDefaults(attempt);

                                try
                                {
                                    attempts++;
                                    var chunk = Deserialize(blob, attempt);
                                    int score = ScoreChunk(chunk);
                                    if (score > bestScore)
                                    {
                                        bestScore = score;
                                        bestChunk = chunk;
                                        bestSettings = attempt;
                                        if (attempt.DispatchMode == 3 && attempt.ProtoFormat == 3)
                                            foundKnownCandidate = true;
                                        if (score >= 450)
                                            return (bestChunk, bestSettings, null);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    bestError ??= ex.Message;
                                }
                            }
                        }
                        if (stopSearch) break;
                    }
                    if (stopSearch) break;
                if (stopSearch) break;
            }
            if (stopSearch) break;
        }

        if (bestChunk != null && bestSettings != null && bestScore >= MinAutoDeserializeScore && LooksPlausibleChunk(bestChunk))
            return (bestChunk, bestSettings, null);

        if (bestChunk != null && bestSettings != null && stopSearch && bestScore > 0 &&
            (LooksPlausibleChunk(bestChunk) || (foundKnownCandidate && bestScore >= MinAutoDeserializeScoreFallback)))
            return (bestChunk, bestSettings, null);

        if (stopSearch)
        {
            bestError = $"Auto-deserialize search stopped after {attempts} attempts / {searchTimer.Elapsed.TotalSeconds:F1}s. {(bestError is null ? "No valid profile found." : $"Last error: {bestError}")}.";
        }

        return (null, null, bestError ?? "All deserialization attempts failed.");
    }

    private static List<int> MergeCandidates(params IEnumerable<int>[] groups)
    {
        var set = new HashSet<int>();
        var merged = new List<int>();
        foreach (var g in groups)
        {
            foreach (var v in g)
            {
                if (v >= 0 && v <= 500000 && set.Add(v))
                    merged.Add(v);
            }
        }

        return merged;
    }

    private static List<LuraphSettings> BuildInferenceProfiles(LuraphSettings preferred)
    {
        var profiles = new List<LuraphSettings>();
        var p = CloneSettings(preferred);
        EnsureRuntimeDefaults(p);
        bool isLikelyM3F3Shape = p.ConstantsOffset == 22172 && p.PrototypesOffset == 98456 && p.InstructionsOffset == 12746;

        if (isLikelyM3F3Shape)
        {
            profiles.Add(new LuraphSettings
            {
                Name = "m3f3:auto",
                ConstantsOffset = 22172,
                PrototypesOffset = 98456,
                InstructionsOffset = 12746,
                DispatchMode = 3,
                FloatTag = 240,
                StringTag = 149,
                IntegerTag = 143,
                ProtoFormat = 3,
                TwoChunk = true,
                BootstrapSkipBytes = 101,
                DecryptStrings = false,
                CharTableInitState = 0,
                CharTableXorMask = 127,
                LcgMultiplier = 65,
                LcgIncrement = 117,
                ApplyConstantTransforms = false,
                BooleanAsVarInt = false,
                OperandModes = new()
                {
                    [0] = OperandSemantic.Constant,
                    [1] = OperandSemantic.RelForward,
                    [2] = OperandSemantic.ClosureRef,
                    [4] = OperandSemantic.RelBackward,
                    [7] = OperandSemantic.Register,
                },
            });
        }

        profiles.Add(p);
        profiles.Add(new LuraphSettings
        {
            Name = "input",
            ConstantsOffset = 98456,
            PrototypesOffset = 12746,
            InstructionsOffset = 22172,
            DispatchMode = 2,
            FloatTag = null,
            StringTag = 13,
            IntegerTag = 199,
            ProtoFormat = 2,
            TwoChunk = true,
            BootstrapSkipBytes = 101,
            DecryptStrings = true,
            CharTableInitState = 0,
            CharTableXorMask = 127,
            LcgMultiplier = 65,
            LcgIncrement = 117,
            ApplyConstantTransforms = true,
            OperandModes = new()
            {
                [6] = OperandSemantic.Register,
                [5] = OperandSemantic.Constant,
                [0] = OperandSemantic.ResolvedConst,
                [1] = OperandSemantic.RelBackward,
                [2] = OperandSemantic.RelForward,
                [7] = OperandSemantic.ProtoIndex,
            },
            OpcodeMap = LuraphLifter.BuildSample2OpcodeMap(),
            FragmentMap = LuraphLifter.BuildSample2FragmentMap(),
            NopOpcodes = LuraphLifter.BuildSample2NopOpcodes(),
        });
        profiles.Add(new LuraphSettings
        {
            Name = "input:v14-auto",
            ConstantsOffset = 22172,
            PrototypesOffset = 98456,
            InstructionsOffset = 12746,
            DispatchMode = 1,
            FloatTag = 82,
            StringTag = 240,
            IntegerTag = null,
            ProtoFormat = 2,
            TwoChunk = true,
            BootstrapSkipBytes = 101,
            DecryptStrings = true,
            CharTableInitState = 0,
            CharTableXorMask = 127,
            LcgMultiplier = 65,
            LcgIncrement = 117,
            ApplyConstantTransforms = false,
            BooleanAsVarInt = true,
            OperandModes = new()
            {
                [0] = OperandSemantic.Constant,
                [1] = OperandSemantic.RelForward,
                [2] = OperandSemantic.ClosureRef,
                [4] = OperandSemantic.RelBackward,
                [7] = OperandSemantic.Register,
            },
        });
        if (!isLikelyM3F3Shape)
        {
            profiles.Add(new LuraphSettings
            {
                Name = "m3f3:auto",
                ConstantsOffset = 22172,
                PrototypesOffset = 12746,
                InstructionsOffset = 98456,
                DispatchMode = 3,
                FloatTag = 240,
                StringTag = 149,
                IntegerTag = 143,
                ProtoFormat = 3,
                TwoChunk = true,
                BootstrapSkipBytes = 101,
                DecryptStrings = false,
                CharTableInitState = 0,
                CharTableXorMask = 127,
                LcgMultiplier = 65,
                LcgIncrement = 117,
                ApplyConstantTransforms = false,
                BooleanAsVarInt = false,
                OperandModes = new()
                {
                    [0] = OperandSemantic.Constant,
                    [1] = OperandSemantic.RelForward,
                    [2] = OperandSemantic.ClosureRef,
                    [4] = OperandSemantic.RelBackward,
                    [7] = OperandSemantic.Register,
                },
            });
        }
        profiles.Add(new LuraphSettings
        {
            Name = "Sample1:auto",
            ConstantsOffset = 231,
            PrototypesOffset = 14954,
            InstructionsOffset = 58516,
            DispatchMode = 1,
            FloatTag = 87,
            StringTag = 216,
            IntegerTag = null,
            ProtoFormat = 3,
            TwoChunk = false,
            BootstrapSkipBytes = 0,
            OperandModes = new()
            {
                [0] = OperandSemantic.Constant,
                [1] = OperandSemantic.RelForward,
                [2] = OperandSemantic.ClosureRef,
                [4] = OperandSemantic.RelBackward,
                [7] = OperandSemantic.Register,
            },
        });
        profiles.Add(new LuraphSettings
        {
            Name = "Generic:auto",
            ConstantsOffset = preferred.ConstantsOffset,
            PrototypesOffset = preferred.PrototypesOffset,
            InstructionsOffset = preferred.InstructionsOffset,
            DispatchMode = preferred.DispatchMode,
            FloatTag = preferred.FloatTag,
            StringTag = preferred.StringTag,
            IntegerTag = preferred.IntegerTag,
            ProtoFormat = preferred.ProtoFormat,
            TwoChunk = false,
            BootstrapSkipBytes = 0,
            DecryptStrings = preferred.DecryptStrings,
            CharTableInitState = preferred.CharTableInitState,
            CharTableXorMask = preferred.CharTableXorMask,
            LcgMultiplier = preferred.LcgMultiplier,
            LcgIncrement = preferred.LcgIncrement,
            ApplyConstantTransforms = preferred.ApplyConstantTransforms,
            OperandModes = new Dictionary<int, OperandSemantic>(preferred.OperandModes),
        });

        return profiles;
    }

    private static List<int> BuildBootstrapCandidates(int preferred)
    {
        var set = new HashSet<int> { preferred, 0, 64, 96, 100, 101, 112, 128, 160, 192, 224, 256 };
        return set.Where(v => v >= 0).OrderBy(v => Math.Abs(v - preferred)).Take(6).ToList();
    }

    private static List<int> BuildOffsetCandidates(byte[] data, LuraphSettings preferred)
    {
        var set = new HashSet<int>
        {
            0, 1, 100, 101, 231, 14954, 58516, 25087, 80260, 94134, preferred.ConstantsOffset, preferred.PrototypesOffset, preferred.InstructionsOffset, };

        void AddWithDeltas(int value)
        {
            if (value < 0 || value > 500000) return;
            set.Add(value);
            foreach (var d in new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024 })
            {
                int v = value - d;
                if (v >= 0) set.Add(v);
            }
        }

        int pos = 0;
        int reads = 0;
        while (pos < data.Length && pos < 4096 && reads < 512)
        {
            int value = 0;
            int multiplier = 1;
            int bytes = 0;
            while (pos < data.Length && bytes < 6)
            {
                byte b = data[pos++];
                int payload = b > 127 ? b - 128 : b;
                value += payload * multiplier;
                multiplier *= 128;
                bytes++;
                if (b < 128) break;
            }

            AddWithDeltas(value);
            reads++;
        }
        return set.OrderBy(v => v).ToList();
    }

    private static List<int> PickClosestCandidates(List<int> candidates, int target, int maxCount)
    {
        return candidates
            .OrderBy(v => Math.Abs(v - target))
            .ThenBy(v => v)
            .Take(maxCount)
            .ToList();
    }

    private static bool LooksPlausibleChunk(LuraphChunk chunk)
    {
        if (chunk == null)
            return false;

        if (chunk.Prototypes.Count <= 0)
            return false;

        if (chunk.EntryIndex < 0 || chunk.EntryIndex >= chunk.Prototypes.Count)
            return false;

        if (chunk.Constants.Count <= 0)
            return false;

        var settings = chunk.Settings;
        int boolConstCount = 0;
        int stringLikeCount = 0;
        int nonTrivialConstCount = 0;
        int suspiciousNumberCount = 0;
        int repeatedPatternCount = 0;
        var numberCounts = new Dictionary<long, int>();
        var opcodeHits = new Dictionary<int, int>();

        foreach (var c in chunk.Constants)
        {
            if (c.Type == LuraphConstantType.Boolean)
                boolConstCount++;

            if (c.Type == LuraphConstantType.String && c.Value is string s && s.Length > 0)
            {
                int printable = 0;
                foreach (var ch in s)
                    if (ch == '\n' || ch == '\r' || ch == '\t' || (ch >= 32 && ch <= 126))
                        printable++;

                if (s.Length > 0 && printable * 100 / s.Length >= 70)
                    stringLikeCount++;
            }
            else if (c.Type == LuraphConstantType.Number && c.Value is double num)
            {
                if (IsLikelyJunkNumber(num))
                    suspiciousNumberCount++;

                long bits = BitConverter.DoubleToInt64Bits(num);
                if (numberCounts.TryGetValue(bits, out var n))
                    numberCounts[bits] = n + 1;
                else
                    numberCounts[bits] = 1;
            }
            else if (c.Type == LuraphConstantType.Integer && c.Value is long value)
            {
                if (value == 0 || value == -1 || value == 1)
                {
                    long bits = value;
                    if (numberCounts.TryGetValue(bits, out var n))
                        numberCounts[bits] = n + 1;
                    else
                        numberCounts[bits] = 1;
                }
            }
        }

        nonTrivialConstCount = chunk.Constants.Count - boolConstCount;

        repeatedPatternCount = numberCounts.Values.Where(v => v >= 4).Sum();
        if (chunk.Constants.Count >= 16 &&
            boolConstCount >= chunk.Constants.Count * 85 / 100 &&
            stringLikeCount == 0 &&
            nonTrivialConstCount <= 1)
            return false;

        if (suspiciousNumberCount >= Math.Max(1, chunk.Constants.Count / 4))
            return false;

        if (repeatedPatternCount >= Math.Max(2, chunk.Constants.Count / 4))
            return false;

        int totalProtoInstr = 0;
        foreach (var proto in chunk.Prototypes)
        {
            totalProtoInstr += proto.InstructionCount;

            int protoOpcodes = 0;
            for (int i = 1; i <= proto.InstructionCount && i < proto.Opcodes.Length; i++)
            {
                int vOp = proto.Opcodes[i];
                protoOpcodes++;

                int slot;
                if (settings?.OpcodeMap != null && settings.OpcodeMap.ContainsKey(vOp)) slot = 0;
                else if (settings?.FragmentMap != null && settings.FragmentMap.ContainsKey(vOp)) slot = 0;
                else if (settings?.NopOpcodes != null && settings.NopOpcodes.Contains(vOp)) slot = 0;
                else slot = 1;

                if (!opcodeHits.TryGetValue(slot, out var hits))
                    opcodeHits[slot] = 0;
                opcodeHits[slot]++;
            }

            if (protoOpcodes > 0 && settings?.OpcodeMap != null && settings?.FragmentMap != null)
            {
                int known = 0;
                for (int i = 1; i <= proto.InstructionCount && i < proto.Opcodes.Length; i++)
                {
                    int vOp = proto.Opcodes[i];
                    if (settings.OpcodeMap.TryGetValue(vOp, out var entry) && entry != null)
                        known++;
                    else if (settings.FragmentMap.TryGetValue(vOp, out var frag) && frag != null)
                        known++;
                    else if (settings.NopOpcodes?.Contains(vOp) == true)
                        known++;
                }

                if (known < protoOpcodes * 2 / 3)
                    return false;
            }
        }

        if (totalProtoInstr <= 0)
            return false;

        if (settings?.OpcodeMap != null && opcodeHits.TryGetValue(0, out var mapped) && opcodeHits.TryGetValue(1, out var unknown))
        {
            if (mapped + unknown > 20 && unknown * 3 > mapped)
                return false;
        }

        return true;
    }

    private static bool IsLikelyJunkNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return true;

        long bits = BitConverter.DoubleToInt64Bits(value);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0x000FFFFFFFFFFFFFL;
        long absSign = bits & 0x7FFFFFFFFFFFFFFF;
        if (exp == 0)
            return true;

        return absSign < 0x0000000100000000L || mantissa == 0;
    }

    private static int ScoreChunk(LuraphChunk chunk)
    {
        if (chunk.Prototypes.Count <= 0)
            return int.MinValue;

        int score = 0;

        if (chunk.EntryProto != null) score += 80;
        if (chunk.EntryIndex >= 0 && chunk.EntryIndex < chunk.Prototypes.Count) score += 40;
        if (chunk.Constants.Count > 0) score += 20;

        score += Math.Min(chunk.Prototypes.Count * 3, 180);
        score += Math.Min(chunk.Constants.Count / 2, 180);

        int totalInstr = 0;
        var opcodes = new HashSet<int>();
        foreach (var proto in chunk.Prototypes)
        {
            if (proto.InstructionCount <= 0 || proto.InstructionCount > 200000)
                return int.MinValue;

            totalInstr += proto.InstructionCount;
            int cap = Math.Min(proto.InstructionCount, proto.Opcodes.Length - 1);
            for (int i = 1; i <= cap; i++)
                opcodes.Add(proto.Opcodes[i]);
        }

        score += Math.Min(totalInstr / 8, 220);
        score += Math.Min(opcodes.Count * 4, 220);

        int stringCount = 0;
        int printableStrings = 0;
        foreach (var c in chunk.Constants)
        {
            if (c.Type != LuraphConstantType.String || c.Value is not string s)
                continue;

            stringCount++;
            int printable = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\t' || ch == '\r' || ch == '\n' || (ch >= 32 && ch <= 126))
                    printable++;
            }

            if (s.Length == 0 || printable >= (s.Length * 6 / 10))
                printableStrings++;
        }

        if (stringCount > 0)
        {
            score += Math.Min(stringCount * 2, 80);
            if (printableStrings * 2 >= stringCount)
                score += 40;
        }

        if (chunk.AntiTamperChunk != null)
            score += 25;

        return score;
    }

    private static void EnsureRuntimeDefaults(LuraphSettings settings)
    {
        settings.OperandModes ??= [];

        if (settings.DispatchMode == 2)
        {
            settings.OperandModes = new Dictionary<int, OperandSemantic>
            {
                [6] = OperandSemantic.Register,
                [5] = OperandSemantic.Constant,
                [0] = OperandSemantic.ResolvedConst,
                [1] = OperandSemantic.RelBackward,
                [2] = OperandSemantic.RelForward,
                [7] = OperandSemantic.ProtoIndex,
            };

            settings.OpcodeMap ??= LuraphLifter.BuildSample2OpcodeMap();
            settings.FragmentMap ??= LuraphLifter.BuildSample2FragmentMap();
            settings.NopOpcodes ??= LuraphLifter.BuildSample2NopOpcodes();
        }
        else if (settings.OperandModes.Count == 0)
        {
            settings.OperandModes = new Dictionary<int, OperandSemantic>
            {
                [0] = OperandSemantic.Constant,
                [1] = OperandSemantic.RelForward,
                [2] = OperandSemantic.ClosureRef,
                [4] = OperandSemantic.RelBackward,
                [7] = OperandSemantic.Register,
            };
        }
    }

    private static LuraphSettings CloneSettings(LuraphSettings s) => new()
    {
        Name = s.Name,
        Description = s.Description,
        ConstantsOffset = s.ConstantsOffset,
        PrototypesOffset = s.PrototypesOffset,
        InstructionsOffset = s.InstructionsOffset,
        DispatchMode = s.DispatchMode,
        FloatTag = s.FloatTag,
        StringTag = s.StringTag,
        IntegerTag = s.IntegerTag,
        ProtoFormat = s.ProtoFormat,
        TwoChunk = s.TwoChunk,
        BootstrapSkipBytes = s.BootstrapSkipBytes,
        DecryptStrings = s.DecryptStrings,
        CharTableInitState = s.CharTableInitState,
        CharTableXorMask = s.CharTableXorMask,
        LcgMultiplier = s.LcgMultiplier,
        LcgIncrement = s.LcgIncrement,
        ApplyConstantTransforms = s.ApplyConstantTransforms,
        BooleanAsVarInt = s.BooleanAsVarInt,
        OperandModes = new Dictionary<int, OperandSemantic>(s.OperandModes),
        OpcodeMap = s.OpcodeMap != null ? new Dictionary<int, OpcodeEntry>(s.OpcodeMap) : null,
        FragmentMap = s.FragmentMap != null ? new Dictionary<int, FragmentEntry>(s.FragmentMap) : null,
        NopOpcodes = s.NopOpcodes != null ? new List<int>(s.NopOpcodes) : null,
    };
}
