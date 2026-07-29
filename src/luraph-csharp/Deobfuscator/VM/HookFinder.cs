using System.Text.RegularExpressions;

namespace LuraphDeobfuscator.Deobfuscator.VM;

public sealed class LuraphHookProfile
{
    public int? ConstantsOffset { get; init; }
    public int? PrototypesOffset { get; init; }
    public int? InstructionsOffset { get; init; }
    public int? ReaderSlot { get; init; }
    public int? VmExecutorSlot { get; init; }
    public int? ProtoDeserializerSlot { get; init; }
    public bool LooksTwoChunk { get; init; }
    public int Confidence { get; init; }
    public List<string> Evidence { get; } = [];
}

public static class HookFinder
{
    static readonly Regex SlotFuncAssign = new(@"\[\s*([0-9A-Fa-fxXbB_]+)\s*\]\s*=\s*\(?\s*function\s*\(", RegexOptions.Compiled);
    static readonly Regex SlotCall = new(@"\[\s*([0-9A-Fa-fxXbB_]+)\s*\]\s*\(\s*\)", RegexOptions.Compiled);
    static readonly Regex ReaderMinusConst = new(@"\[\s*([0-9A-Fa-fxXbB_]+)\s*\]\s*\(\s*\)\s*-\s*([0-9A-Fa-fxXbB_]+)", RegexOptions.Compiled);

    public static LuraphHookProfile Run(string source)
    {
        var profile = new LuraphHookProfile();
        if (string.IsNullOrWhiteSpace(source))
            return profile;

        var funcAssignMatches = SlotFuncAssign.Matches(source)
            .Select(m => new { Match = m, Slot = ParseLuaInt(m.Groups[1].Value) })
            .Where(x => x.Slot.HasValue)
            .Select(x => new { x.Match, Slot = x.Slot!.Value })
            .ToList();

        var funcSlots = funcAssignMatches
            .Select(x => x.Slot)
            .ToHashSet();

        var callSlots = SlotCall.Matches(source)
            .Select(m => ParseLuaInt(m.Groups[1].Value))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var readerHooks = ReaderMinusConst.Matches(source)
            .Select(m =>
            {
                var slot = ParseLuaInt(m.Groups[1].Value);
                var offs = ParseLuaInt(m.Groups[2].Value);
                return new
                {
                    Slot = slot,
                    Offset = offs,
                    Pos = m.Index,
                };
            })
            .Where(x => x.Slot.HasValue && x.Offset.HasValue && x.Offset.Value > 0)
            .Select(x => new { Slot = x.Slot!.Value, Offset = x.Offset!.Value, x.Pos })
            .ToList();

        int confidence = 0;
        var evidence = new List<string>();

        int? readerSlot = null;
        int? constantsOffset = null;
        int? prototypesOffset = null;
        int? instructionsOffset = null;

        if (readerHooks.Count > 0)
        {
            var chosenReader = readerHooks
                .GroupBy(x => x.Slot)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Select(v => v.Offset).Distinct().Count())
                .First();

            readerSlot = chosenReader.Key;
            confidence += 35;
            evidence.Add($"reader-slot:{readerSlot} hooks={chosenReader.Count()}");

            var orderedDistinctOffsets = chosenReader
                .OrderBy(x => x.Pos)
                .Select(x => x.Offset)
                .Distinct()
                .ToList();
            var roleScore = new Dictionary<int, (int Const, int Proto, int Instr)>();
            foreach (var hit in chosenReader)
            {
                if (!roleScore.ContainsKey(hit.Offset))
                    roleScore[hit.Offset] = (0, 0, 0);

                int start = Math.Max(0, hit.Pos - 80);
                int len = Math.Min(520, source.Length - start);
                var seg = source.Substring(start, len);

                var score = roleScore[hit.Offset];

                if (Regex.IsMatch(seg, @"\*\s*3")) score.Proto += 5;
                if (Regex.IsMatch(seg, @"\[\s*13\s*\]\s*=.*\*\s*3")) score.Proto += 4;
                if (Regex.IsMatch(seg, @"\[[ ]*36[ ]*\]\s*\(\s*\)\s*\]\s*")) score.Proto += 2;

                if (Regex.IsMatch(seg, @"\[\s*27\s*\]\s*\(\s*\)")) score.Const += 4;
                if (Regex.IsMatch(seg, @"~=\s*0")) score.Const += 2;
                if (Regex.IsMatch(seg, @"\[\s*15\s*\]\s*=\s*\w+\[\s*26\s*\]\s*\(")) score.Const += 3;

                if (Regex.IsMatch(seg, @"\[\s*26\s*\]\s*\(")) score.Instr += 2;
                if (Regex.IsMatch(seg, @"return\s+\w+\s*,\s*\d{3,6}")) score.Instr += 1;

                roleScore[hit.Offset] = score;
            }

            int PickBy(Func<(int Const, int Proto, int Instr), int> projector, HashSet<int> excluded)
            {
                return roleScore
                    .Where(kv => !excluded.Contains(kv.Key))
                    .OrderByDescending(kv => projector(kv.Value))
                    .ThenBy(kv => kv.Key)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
            }

            var used = new HashSet<int>();

            int pickedProto = PickBy(s => s.Proto, used);
            if (pickedProto != 0)
            {
                prototypesOffset = pickedProto;
                used.Add(pickedProto);
                confidence += 15;
            }

            int pickedConst = PickBy(s => s.Const, used);
            if (pickedConst != 0)
            {
                constantsOffset = pickedConst;
                used.Add(pickedConst);
                confidence += 15;
            }

            int pickedInstr = PickBy(s => s.Instr, used);
            if (pickedInstr != 0)
            {
                instructionsOffset = pickedInstr;
                used.Add(pickedInstr);
                confidence += 15;
            }
            foreach (var off in orderedDistinctOffsets)
            {
                if (!constantsOffset.HasValue) { constantsOffset = off; continue; }
                if (!prototypesOffset.HasValue && off != constantsOffset.Value) { prototypesOffset = off; continue; }
                bool differentFromProto = !prototypesOffset.HasValue || off != prototypesOffset.Value;
                if (!instructionsOffset.HasValue && off != constantsOffset.Value && differentFromProto)
                {
                    instructionsOffset = off;
                    break;
                }
            }

            evidence.Add($"offsets:{string.Join(",", orderedDistinctOffsets.Take(5))}");
            evidence.Add($"roles:c={constantsOffset?.ToString() ?? "?"},p={prototypesOffset?.ToString() ?? "?"},i={instructionsOffset?.ToString() ?? "?"}");
        }

        int? vmExecutorSlot = null;
        int? protoDeserializerSlot = null;
        bool looksTwoChunk = false;

        var candidateFunctionSlots = funcSlots
            .Where(s => s >= 25)
            .OrderBy(s => s)
            .ToList();

        if (candidateFunctionSlots.Count > 0)
        {
            if (funcSlots.Contains(40)) protoDeserializerSlot = 40;
            if (funcSlots.Contains(39)) vmExecutorSlot = 39;
            if (readerSlot.HasValue && funcAssignMatches.Count > 0)
            {
                foreach (var f in funcAssignMatches)
                {
                    int start = f.Match.Index;
                    int end = source.Length;
                    var next = funcAssignMatches.FirstOrDefault(x => x.Match.Index > start);
                    if (next != null) end = next.Match.Index;

                    int len = Math.Min(Math.Max(end - start, 256), 6000);
                    var seg = source.Substring(start, Math.Min(len, source.Length - start));
                    if (protoDeserializerSlot == null)
                    {
                        if (seg.Contains($"[{readerSlot.Value}]()", StringComparison.Ordinal) ||
                            seg.Contains($"[{readerSlot.Value}] ()", StringComparison.Ordinal))
                        {
                            if (f.Slot >= 24)
                                protoDeserializerSlot = f.Slot;
                        }
                    }
                    var nestedAssignedSlots = SlotFuncAssign.Matches(seg)
                        .Select(m => ParseLuaInt(m.Groups[1].Value))
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .Where(v => v != f.Slot)
                        .ToList();

                    if (nestedAssignedSlots.Count > 0)
                    {
                        var candidateVm = nestedAssignedSlots.Where(v => v >= 24).DefaultIfEmpty(nestedAssignedSlots.Max()).Max();
                        if (vmExecutorSlot == null || candidateVm > vmExecutorSlot)
                            vmExecutorSlot = candidateVm;
                    }
                }
            }
            protoDeserializerSlot ??= candidateFunctionSlots.Where(v => v >= 24).DefaultIfEmpty(candidateFunctionSlots[^1]).Max();
            if (candidateFunctionSlots.Count >= 2)
                vmExecutorSlot ??= candidateFunctionSlots[^2];

            confidence += protoDeserializerSlot.HasValue ? 10 : 0;
            confidence += vmExecutorSlot.HasValue ? 10 : 0;
            bool adjacentHighSlots = candidateFunctionSlots.Count >= 2 &&
                                     candidateFunctionSlots[^1] - candidateFunctionSlots[^2] <= 1;
            bool callsHighSlot = callSlots.Any(s => s >= (protoDeserializerSlot ?? 0));
            looksTwoChunk = adjacentHighSlots && callsHighSlot;
            if (looksTwoChunk) confidence += 10;

            evidence.Add($"function-slots:{string.Join(",", candidateFunctionSlots.TakeLast(4))}");
            evidence.Add($"high-slot-calls:{callSlots.Count(s => s >= 25)}");
        }

        var result = new LuraphHookProfile
        {
            ConstantsOffset = constantsOffset,
            PrototypesOffset = prototypesOffset,
            InstructionsOffset = instructionsOffset,
            ReaderSlot = readerSlot,
            VmExecutorSlot = vmExecutorSlot,
            ProtoDeserializerSlot = protoDeserializerSlot,
            LooksTwoChunk = looksTwoChunk,
            Confidence = Math.Min(confidence, 100),
        };

        foreach (var item in evidence)
            result.Evidence.Add(item);

        return result;
    }

    private static int? ParseLuaInt(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var t = token.Replace("_", "", StringComparison.Ordinal).Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ConvertHex(t[2..], out var v)) return v;
            return null;
        }
        if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            if (ConvertBin(t[2..], out var v)) return v;
            return null;
        }

        if (int.TryParse(t, out var dec)) return dec;
        return null;
    }

    private static bool ConvertHex(string s, out int value)
    {
        value = 0;
        try
        {
            value = Convert.ToInt32(s, 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ConvertBin(string s, out int value)
    {
        value = 0;
        if (s.Length == 0) return false;

        foreach (var ch in s)
        {
            if (ch != '0' && ch != '1') return false;
            value = (value << 1) + (ch - '0');
        }
        return true;
    }
}
