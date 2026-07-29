using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LuraphDeobfuscator.Deobfuscator.AST;
using LuraphDeobfuscator.Deobfuscator.CFG;
using LuraphDeobfuscator.Deobfuscator.IR;
using LuraphDeobfuscator.Deobfuscator.Passes;
using LuraphDeobfuscator.Deobfuscator.Printer;
using LuraphDeobfuscator.Deobfuscator.Structuring;
using LuraphDeobfuscator.Deobfuscator.VM;

return CLI.Run(args);

static class CLI
{
    static readonly Dictionary<string, LuraphSettings> KnownBuilds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sample2"] = new LuraphSettings
        {
            Name = "Sample2",
            ConstantsOffset = 25087,
            PrototypesOffset = 80260,
            InstructionsOffset = 94134,
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
        },
        ["Sample1"] = new LuraphSettings
        {
            Name = "Sample1",
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
        },
    };

    class ParsedArgs
    {
        public string? Command { get; set; }
        public string? File { get; set; }
        public bool Help { get; set; }
        public Dictionary<string, string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetFlag(string key) => Flags.TryGetValue(key, out var v) ? v : null;
        public bool HasFlag(string key) => Flags.ContainsKey(key);
        public int GetIntFlag(string key, int fallback) => Flags.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
    }

    static ParsedArgs ParseArgs(string[] args)
    {
        var parsed = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h")
            {
                parsed.Help = true;
            }
            else if (a.StartsWith("--"))
            {
                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    i++;
                    parsed.Flags[key] = args[i];
                }
                else
                {
                    parsed.Flags[key] = "true";
                }
            }
            else if (parsed.Command == null)
            {
                parsed.Command = a;
            }
            else if (parsed.File == null)
            {
                parsed.File = a;
            }
        }
        return parsed;
    }

    static void PrintUsage()
    {
        Console.WriteLine("""

        Luraph Deobfuscator - C# Decompiler Pipeline
        ----------------------------------------------

        Usage: LuraphDeobfuscator <command> <file> [options]

        Commands:
          decompile <file>   Full pipeline: extract -> deserialize -> lift -> CFG -> AST -> Lua source
          disasm <file>      Deserialize and dump lifted IR disassembly
          dump-cfg <file>    Deserialize, lift, and dump the control flow graph
          analyze <file>     Deserialize and print structure info (constants, protos, etc.)
          infer-hooks <file> Infer hook/offset hints directly from Luraph source
          detect <file>      Quick check if a file is Luraph-obfuscated
          save-config        Export a build config to JSON (for editing or new builds)

        Options:
          --build <name>       Optional legacy profile override: Sample1, Sample2
          --config <path>      Load build config from a JSON file (overrides --build)
          --output <path>      Output file path (default: <input>.deob.lua or stdout)
          --chunk <1|2>        Which chunk to process in 2-chunk mode (default: 2)
                                 1 = anti-tamper bootstrapper
                                 2 = user script
          --proto <n>          Process only prototype N (default: all)
          --verbose            Show extra debug output during each pipeline stage
          --no-cache           Disable inferred hook-profile cache for this run

          --constants <n>      Override constants offset
          --prototypes <n>     Override prototypes offset
          --instructions <n>   Override instructions offset

          --help, -h           Show this help message

        Examples:
          LuraphDeobfuscator decompile Samples/Sample2.lua
          LuraphDeobfuscator decompile Samples/Sample2.lua --output out.lua
          LuraphDeobfuscator decompile Samples/Sample2.lua --config myBuild.json
          LuraphDeobfuscator decompile Samples/Sample2.lua --chunk 1 --verbose
          LuraphDeobfuscator save-config --build Sample2 --output Sample2.config.json
          LuraphDeobfuscator disasm Samples/Sample2.lua --chunk 2 --proto 0
          LuraphDeobfuscator dump-cfg Samples/Sample2.lua --proto 0
          LuraphDeobfuscator infer-hooks Samples/Sample2.lua --output inferred.json
          LuraphDeobfuscator analyze Samples/Sample2.lua --build Sample2
          LuraphDeobfuscator detect Samples/Sample2.lua

        Known builds:
        """);

        foreach (var (name, s) in KnownBuilds)
        {
            Console.WriteLine($"  {name,-12}  dispatch={s.DispatchMode}  format=V{s.ProtoFormat}  " + $"twoChunk={s.TwoChunk}  offsets=({s.ConstantsOffset}, {s.PrototypesOffset}, {s.InstructionsOffset})");
        }

        Console.WriteLine();
    }

    public static int Run(string[] args)
    {
        var parsed = ParseArgs(args);

        if (parsed.Help || parsed.Command == null)
        {
            PrintUsage();
            return parsed.Help ? 0 : 1;
        }

        return parsed.Command.ToLowerInvariant() switch
        {
            "decompile" => CmdDecompile(parsed),
            "disasm" => CmdDisasm(parsed),
            "dump-cfg" => CmdDumpCFG(parsed),
            "analyze" => CmdAnalyze(parsed),
            "infer-hooks" => CmdInferHooks(parsed),
            "detect" => CmdDetect(parsed),
            "save-config" => CmdSaveConfig(parsed),
            _ => Error($"Unknown command: '{parsed.Command}'. Run with --help for usage.")
        };
    }

    static int CmdSaveConfig(ParsedArgs p)
    {
        var buildName = p.GetFlag("build") ?? "Sample2";
        if (!KnownBuilds.TryGetValue(buildName, out var settings))
            return Error($"Unknown build: '{buildName}'. Available: {string.Join(", ", KnownBuilds.Keys)}");

        var full = CloneSettings(settings);
        full.OpcodeMap ??= LuraphLifter.BuildSample2OpcodeMap();
        full.FragmentMap ??= LuraphLifter.BuildSample2FragmentMap();
        full.NopOpcodes ??= LuraphLifter.BuildSample2NopOpcodes();

        var outPath = p.GetFlag("output") ?? $"{buildName}.config.json";
        full.SaveToJson(outPath);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[+] Config saved: {outPath}");
        Console.ResetColor();
        Console.WriteLine($"    Build: {buildName}");
        Console.WriteLine($"    Opcodes: {full.OpcodeMap.Count}");
        Console.WriteLine($"    Fragments: {full.FragmentMap.Count}");
        Console.WriteLine($"    NOPs: {full.NopOpcodes.Count}");

        return 0;
    }

    static int CmdDetect(ParsedArgs p)
    {
        var (source, err) = ReadInput(p);
        if (source == null) return Error(err!);

        bool hasLPH = source.Contains("LPH");
        bool hasBase85Pattern = source.Contains("!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~"[..10]);
        bool hasBlobLiteral = source.Contains("LPH") && (source.Contains("[[") || source.Contains("\"LPH"));

        if (hasLPH)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[+] Luraph detected: YES");
            Console.ResetColor();

            if (hasBlobLiteral)
                Console.WriteLine("    LPH blob found in string literal");
            else
                Console.WriteLine("    LPH prefix found (raw blob or non-standard wrapper)");
            string blob = ExtractBlob(source);
            Console.WriteLine($"    Blob size: {blob.Length:N0} characters");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[-] Luraph detected: NO");
            Console.ResetColor();
            Console.WriteLine("    No LPH prefix found in source");
        }

        return 0;
    }

    static int CmdAnalyze(ParsedArgs p)
    {
        var (source, err) = ReadInput(p);
        if (source == null) return Error(err!);

        bool verbose = p.HasFlag("verbose");
        var settings = ResolveSettings(p, source);
        string blob = ExtractBlob(source);
        Console.WriteLine($"[*] Blob extracted: {blob.Length:N0} characters");
        var (chunk, chunk1, dErr) = DeserializeBlob(blob, settings, p);
        if (chunk == null) return Error($"Deserialization failed: {dErr}");

        int chunkNum = p.GetIntFlag("chunk", 2);
        if (chunk1 != null)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Architecture: 2-chunk (anti-tamper + user script)");
            Console.ResetColor();

            Console.WriteLine($"  Chunk 1 (anti-tamper): {chunk1.Constants.Count} constants, " + $"{chunk1.Prototypes.Count} protos, entry=P{chunk1.EntryIndex}");
            Console.WriteLine($"  Chunk 2 (user script): {chunk.Constants.Count} constants, " + $"{chunk.Prototypes.Count} protos, entry=P{chunk.EntryIndex}");
            Console.WriteLine($"  Selected: chunk {chunkNum}");
        }
        else
        {
            Console.WriteLine($"\nArchitecture: single-chunk");
        }
        var target = chunkNum == 1 && chunk1 != null ? chunk1 : chunk;

        Console.WriteLine();
        Console.WriteLine($"Constants:    {target.Constants.Count}");
        Console.WriteLine($"Prototypes:   {target.Prototypes.Count}");
        Console.WriteLine($"Entry:        P{target.EntryIndex}");
        Console.WriteLine($"Cached:       {target.IsCached}");

        Console.WriteLine();
        Console.WriteLine("Prototype details:");
        for (int i = 0; i < target.Prototypes.Count; i++)
        {
            var proto = target.Prototypes[i];
            Console.WriteLine($"  P{i}: {proto.InstructionCount} instrs, stack={proto.StackSize}, " + $"params={proto.NumParams}, upvals={proto.UpvalueCount}");
        }

        if (verbose)
        {
            Console.WriteLine();
            Console.WriteLine("Constant pool:");
            for (int i = 0; i < Math.Min(target.Constants.Count, 50); i++)
            {
                var c = target.Constants[i];
                string extra = "";
                if (c.RawBytes != null && c.RawBytes.Length > 0)
                    extra = $"  raw=[{string.Join(",", c.RawBytes.Select(b => b.ToString()))}]";
                Console.WriteLine($"  K{i}: {c}{extra}");
            }
            if (target.Constants.Count > 50)
                Console.WriteLine($"  ... ({target.Constants.Count - 50} more)");
        }

        return 0;
    }

    static int CmdInferHooks(ParsedArgs p)
    {
        var (source, err) = ReadInput(p);
        if (source == null) return Error(err!);

        var inferred = BuildHookInferredSettings(source);
        var hooks = HookFinder.Run(source);

        Console.WriteLine("Hook inference from source:");
        Console.WriteLine($"  constants offset:    {inferred.ConstantsOffset}");
        Console.WriteLine($"  prototypes offset:   {inferred.PrototypesOffset}");
        Console.WriteLine($"  instructions offset: {inferred.InstructionsOffset}");
        Console.WriteLine($"  twoChunk:            {inferred.TwoChunk}");
        Console.WriteLine($"  dispatch/format:     {inferred.DispatchMode}/V{inferred.ProtoFormat}");
        Console.WriteLine($"  reader slot:         {hooks.ReaderSlot?.ToString() ?? "?"}");
        Console.WriteLine($"  vm/proto slots:      {hooks.VmExecutorSlot?.ToString() ?? "?"}/{hooks.ProtoDeserializerSlot?.ToString() ?? "?"}");
        Console.WriteLine($"  confidence:          {hooks.Confidence}");
        if (hooks.Evidence.Count > 0)
            Console.WriteLine($"  evidence:            {string.Join(" | ", hooks.Evidence)}");

        if (!string.IsNullOrWhiteSpace(p.File))
        {
            SaveInferredSettingsToCache(p.File!, source, inferred, out var cachePath);
            Console.WriteLine($"  cache:               {cachePath}");
        }

        var outPath = p.GetFlag("output");
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            EnsureDirectory(outPath);
            inferred.SaveToJson(outPath);
            Console.WriteLine($"  saved config:        {outPath}");
        }

        return 0;
    }

    static int CmdDisasm(ParsedArgs p)
    {
        var (source, err) = ReadInput(p);
        if (source == null) return Error(err!);

        bool verbose = p.HasFlag("verbose");
        var settings = ResolveSettings(p, source);

        string blob = ExtractBlob(source);
        Log($"Blob extracted: {blob.Length:N0} characters");

        var (chunk, chunk1, dErr) = DeserializeBlob(blob, settings, p);
        if (chunk == null) return Error($"Deserialization failed: {dErr}");

        int chunkNum = p.GetIntFlag("chunk", 2);
        var target = chunkNum == 1 && chunk1 != null ? chunk1 : chunk;
        int? protoFilter = p.GetFlag("proto") != null ? p.GetIntFlag("proto", -1) : null;

        Log($"Deserialised chunk {chunkNum}: {target.Constants.Count} constants, " + $"{target.Prototypes.Count} protos, entry=P{target.EntryIndex}");
        settings = target.Settings;
        var lifter = new LuraphLifter(settings);
        var sb = new StringBuilder();

        sb.AppendLine($"; Luraph IR Disassembly - chunk {chunkNum}");
        sb.AppendLine($"; {target.Constants.Count} constants, {target.Prototypes.Count} prototypes, entry=P{target.EntryIndex}");
        sb.AppendLine();

        for (int i = 0; i < target.Prototypes.Count; i++)
        {
            if (protoFilter.HasValue && protoFilter.Value != i) continue;

            var proto = target.Prototypes[i];
            var ir = lifter.Lift(proto);

            sb.AppendLine($"; {'=',0} Proto {i} {'=',0}".PadRight(70, '='));
            sb.AppendLine($"; {proto.InstructionCount} vm instrs -> {ir.Count} IR instrs" + $"  |  stack={proto.StackSize}  params={proto.NumParams}  upvals={proto.UpvalueCount}");
            sb.AppendLine();

            for (int j = 0; j < ir.Count; j++)
            {
                var instr = ir[j];
                sb.AppendLine($"  {j,4}  {instr.Format()}");
            }

            sb.AppendLine();
        }

        string disasm = sb.ToString();
        var outPath = p.GetFlag("output");
        if (outPath != null)
        {
            EnsureDirectory(outPath);
            File.WriteAllText(outPath, disasm);
            Log($"Disassembly written to {outPath} ({disasm.Split('\n').Length} lines)");
        }
        else
        {
            Console.WriteLine();
            Console.Write(disasm);
        }

        return 0;
    }

    static int CmdDumpCFG(ParsedArgs p)
    {
        var (source, err) = ReadInput(p);
        if (source == null) return Error(err!);

        var settings = ResolveSettings(p, source);
        string blob = ExtractBlob(source);

        var (chunk, chunk1, dErr) = DeserializeBlob(blob, settings, p);
        if (chunk == null) return Error($"Deserialization failed: {dErr}");

        int chunkNum = p.GetIntFlag("chunk", 2);
        var target = chunkNum == 1 && chunk1 != null ? chunk1 : chunk;
        int? protoFilter = p.GetFlag("proto") != null ? p.GetIntFlag("proto", -1) : null;

        settings = target.Settings;
        var lifter = new LuraphLifter(settings);
        var sb = new StringBuilder();

        sb.AppendLine($"IR + CFG Dump - chunk {chunkNum}");
        sb.AppendLine();

        for (int i = 0; i < target.Prototypes.Count; i++)
        {
            if (protoFilter.HasValue && protoFilter.Value != i) continue;

            var proto = target.Prototypes[i];
            var ir = lifter.Lift(proto);
            var blocks = CFGBuilder.Build(ir);

            DominatorTree.Compute(blocks);
            var loops = LoopAnalyzer.DetectLoops(blocks);
            SSABuilder.Transform(blocks);
            int phiCount = SSABuilder.CountPhis(blocks);
            int defCount = SSABuilder.CountDefinitions(blocks);

            sb.AppendLine(new string('-', 60));
            sb.AppendLine($"Proto {i}: {ir.Count} IR instrs, {blocks.Count} blocks, {loops.Count} loops, {phiCount} phis, {defCount} defs");
            sb.AppendLine(new string('-', 60));

            foreach (var block in blocks)
            {
                string flags = "";
                if (block.IsLoopHeader) flags += " [LOOP-HEADER]";
                if (block.IsUnreachable) flags += " [UNREACHABLE]";
                if (block.IsExit) flags += " [EXIT]";
                if (block.LoopDepth > 0) flags += $" [depth={block.LoopDepth}]";

                var idom = block.ImmediateDominator != null ? $" idom={block.ImmediateDominator.Label}" : "";
                var preds = block.Predecessors.Count > 0
                    ? " <- " + string.Join(", ", block.Predecessors.Select(b => b.Label))
                    : "";
                var succs = block.Successors.Count > 0
                    ? " -> " + string.Join(", ", block.Successors.Select(b => b.Label))
                    : "";

                sb.AppendLine($"  {block.Label} [{block.StartIndex}-{block.EndIndex}]" + $"  ({block.Instructions.Count}+{(block.Terminator != null ? 1 : 0)} instrs){flags}{idom}");
                if (preds.Length > 0) sb.AppendLine($"    preds:{preds}");
                if (succs.Length > 0) sb.AppendLine($"    succs:{succs}");
                foreach (var instr in block.Instructions)
                    sb.AppendLine($"      {instr.Format()}");
                if (block.Terminator != null)
                    sb.AppendLine($"    > {block.Terminator.Format()}");

                sb.AppendLine();
            }

            if (loops.Count > 0)
            {
                sb.AppendLine("  Detected loops:");
                foreach (var loop in loops)
                {
                    var bodyLabels = string.Join(", ", loop.Body.OrderBy(b => b.Id).Select(b => b.Label));
                    var exits = loop.ExitBlocks.Count > 0
                        ? " exits=" + string.Join(", ", loop.ExitBlocks.Select(b => b.Label))
                        : "";
                    sb.AppendLine($"    {loop.Kind} @ {loop.Header.Label}, latch={loop.Latch.Label}, " + $"body=[{bodyLabels}]{exits}");
                }
                sb.AppendLine();
            }
        }

        string dump = sb.ToString();

        var outPath = p.GetFlag("output");
        if (outPath != null)
        {
            EnsureDirectory(outPath);
            File.WriteAllText(outPath, dump);
            Log($"CFG dump written to {outPath}");
        }
        else
        {
            Console.Write(dump);
        }

        return 0;
    }

    static int CmdDecompile(ParsedArgs p)
    {
        var (source, err) = ReadInput(p);
        if (source == null) return Error(err!);

        bool verbose = p.HasFlag("verbose");
        var settings = ResolveSettings(p, source);
        var sw = Stopwatch.StartNew();
        if (!source.Contains("LPH", StringComparison.Ordinal))
        {
            var noVmOut = p.GetFlag("output");
            if (noVmOut == null && p.File != null)
                noVmOut = Path.ChangeExtension(p.File, ".deob.lua");
            noVmOut ??= "output.deob.lua";

            EnsureDirectory(noVmOut);
            File.WriteAllText(noVmOut, source);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[!] No LPH VM blob detected. Emitted source in no-VM mode.");
            Console.ResetColor();
            Console.WriteLine($"    Output: {noVmOut}");
            return 0;
        }
        Step(1, 6, "Extracting bytecode blob...");
        string blob = ExtractBlob(source);
        Log($"  Blob: {blob.Length:N0} characters");
        Step(2, 6, "Deserializing bytecode...");
        var (chunk, chunk1, dErr) = DeserializeBlob(blob, settings, p);
        if (chunk == null) return Error($"Deserialization failed: {dErr}");

        int chunkNum = p.GetIntFlag("chunk", 2);
        var target = chunkNum == 1 && chunk1 != null ? chunk1 : chunk;

        if (chunk1 != null)
            Log($"  2-chunk mode: selected chunk {chunkNum}");
        Log($"  {target.Constants.Count} constants, {target.Prototypes.Count} prototypes, entry=P{target.EntryIndex}");
        Log($"  Cached: {target.IsCached}");
        if (verbose)
        {
            int shown = 0;
            for (int ci = 0; ci < target.Constants.Count && shown < 30; ci++)
            {
                var c = target.Constants[ci];
                if (c.Type == LuraphConstantType.String)
                {
                    string rawHex = c.RawBytes != null
                        ? string.Join(" ", c.RawBytes.Select(b => b.ToString("X2")))
                        : "(no raw)";
                    Log($"  K{ci}: type=string  val=\"{c.Value}\"  raw=[{rawHex}] len={c.RawBytes?.Length ?? 0}");
                    shown++;
                }
                else
                {
                    if (shown < 30)
                        Log($"  K{ci}: type={c.Type}  val={c.Value}");
                }
            }
        }
        Step(3, 6, "Lifting to IR...");
        settings = target.Settings;
        var lifter = new LuraphLifter(settings);
        var allProtoIR = new Dictionary<int, List<IRInstr>>();

        for (int i = 0; i < target.Prototypes.Count; i++)
        {
            var proto = target.Prototypes[i];
            var ir = lifter.Lift(proto);
            allProtoIR[i] = ir;
        }
        Log($"  Lifted {allProtoIR.Count} prototypes");
        NumParamsInferrer.InferAll(allProtoIR, target.Prototypes);

        if (verbose)
        {
            for (int i = 0; i < target.Prototypes.Count; i++)
            {
                var proto = target.Prototypes[i];
                var ir = allProtoIR[i];
                Log($"  P{i}: {proto.InstructionCount} vm instrs -> {ir.Count} IR instrs, params={proto.NumParams}");
            }
        }
        Step(4, 6, "Building CFG, SSA, running optimization passes...");
        var allProtoASTs = new Dictionary<int, ASTBlock>();

        int totalNops = 0, totalDCE = 0, totalFolded = 0, totalCopied = 0;

        for (int protoIdx = 0; protoIdx < target.Prototypes.Count; protoIdx++)
        {
            if (!allProtoIR.TryGetValue(protoIdx, out var ir)) continue;

            try
            {
                var blocks = CFGBuilder.Build(ir);
                if (blocks.Count == 0)
                {
                    allProtoASTs[protoIdx] = new ASTBlock();
                    continue;
                }
                DominatorTree.Compute(blocks);
                var loops = LoopAnalyzer.DetectLoops(blocks);
                try
                {
                    SSABuilder.Transform(blocks);
                    SSABuilder.BuildNameHints(blocks, target.Prototypes[protoIdx].NumParams);
                }
                catch
                {
                    // if ssa dies keep going with a non-SSA CFG (some passes will be skipped, and structuring may be worse, but better than nothing)   
                }
                totalNops += NopEliminator.Run(blocks);
                totalFolded += ConstantFolder.Run(blocks);
                totalCopied += CopyPropagator.Run(blocks);
                totalDCE += DeadCodeEliminator.Run(blocks);
                var structurer = new ControlFlowStructurer(blocks, loops);
                var ast = structurer.Structure();
                allProtoASTs[protoIdx] = ast;

                if (verbose)
                    Log($"  P{protoIdx}: {blocks.Count} blocks, {loops.Count} loops -> {ast.Statements.Count} stmts");
            }
            catch (Exception ex)
            {
                if (verbose)
                    Log($"  P{protoIdx}: structuring failed ({ex.Message}), falling back to flat IR");
                var fallback = new ASTBlock();
                foreach (var instr in ir)
                {
                    var stmt = StatementBuilder.FromInstruction(instr);
                    if (stmt != null) fallback.Statements.Add(stmt);
                }
                allProtoASTs[protoIdx] = fallback;
            }
        }

        Log($"  Passes: nops={totalNops}, folded={totalFolded}, copied={totalCopied}, dce={totalDCE}");
        Step(5, 6, "Linking prototypes and folding expressions...");
        var fullAST = ProtoLinker.BuildFullOutput(target.EntryIndex, allProtoASTs, target.Prototypes);
        ExpressionFolder.Fold(fullAST);
        int entryNumParams = target.EntryIndex >= 0 && target.EntryIndex < target.Prototypes.Count
            ? target.Prototypes[target.EntryIndex].NumParams
            : 0;
        LocalDeclarationPass.Apply(fullAST, entryNumParams);
        Step(6, 6, "Printing Lua output...");
        string output = LuaPrinter.Print(fullAST);
        var outPath = p.GetFlag("output");
        if (outPath == null && p.File != null)
            outPath = Path.ChangeExtension(p.File, ".deob.lua");
        outPath ??= "output.deob.lua";

        EnsureDirectory(outPath);
        File.WriteAllText(outPath, output);

        sw.Stop();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[+] Done in {sw.Elapsed.TotalSeconds:F2}s");
        Console.ResetColor();
        Console.WriteLine($"    Output: {outPath}");
        Console.WriteLine($"    Lines:  {output.Split('\n').Length}");
        Console.WriteLine($"    Size:   {output.Length:N0} bytes");
        Console.WriteLine($"    Protos: {allProtoASTs.Count}");

        return 0;
    }
    static (string? source, string? error) ReadInput(ParsedArgs p)
    {
        if (p.File == null)
            return (null, "No input file specified. Run with --help for usage.");

        if (!File.Exists(p.File))
            return (null, $"File not found: {p.File}");

        return (File.ReadAllText(p.File), null);
    }
    static LuraphSettings ResolveSettings(ParsedArgs p, string source)
    {
        LuraphSettings settings;
        var configPath = p.GetFlag("config");
        if (configPath != null)
        {
            if (!File.Exists(configPath))
            {
                Warn($"Config file not found: {configPath}, falling back to defaults");
                settings = new LuraphSettings();
            }
            else
            {
                settings = LuraphSettings.LoadFromJson(configPath);
                Log($"Loaded config from: {configPath} (build: {settings.Name ?? "unnamed"})");
            }
        }
        else
        {
            var buildName = p.GetFlag("build");
            if (buildName != null)
            {
                if (KnownBuilds.TryGetValue(buildName, out var known))
                {
                    settings = CloneSettings(known);
                    Log($"Using build config: {buildName}");
                }
                else
                {
                    Warn($"Unknown build '{buildName}', falling back to defaults");
                    settings = new LuraphSettings();
                }
            }
            else
            {
                bool allowCache = !p.HasFlag("no-cache");
                bool hasInputPath = !string.IsNullOrWhiteSpace(p.File);

                if (allowCache && hasInputPath && TryLoadInferredSettingsFromCache(p.File!, source, out var cached, out var cachePath))
                {
                    settings = cached;
                    Log($"Loaded inferred hook profile from cache: {cachePath}");
                }
                else
                {
                    settings = BuildHookInferredSettings(source);
                    Log($"Using dynamic hook-inferred baseline: offsets=({settings.ConstantsOffset}, {settings.PrototypesOffset}, {settings.InstructionsOffset})");

                    if (allowCache && hasInputPath)
                    {
                        SaveInferredSettingsToCache(p.File!, source, settings, out var savedPath);
                        Log($"Saved inferred hook profile cache: {savedPath}");
                    }
                }
            }
        }
        if (p.GetFlag("constants") != null)
            settings.ConstantsOffset = p.GetIntFlag("constants", settings.ConstantsOffset);
        if (p.GetFlag("prototypes") != null)
            settings.PrototypesOffset = p.GetIntFlag("prototypes", settings.PrototypesOffset);
        if (p.GetFlag("instructions") != null)
            settings.InstructionsOffset = p.GetIntFlag("instructions", settings.InstructionsOffset);

        return settings;
    }

    static LuraphSettings BuildHookInferredSettings(string source)
    {
        var settings = CloneSettings(KnownBuilds["Sample2"]);
        settings.Name = "dynamic:hooks";
        var hook = HookFinder.Run(source);

        if (hook.ConstantsOffset.HasValue) settings.ConstantsOffset = hook.ConstantsOffset.Value;
        if (hook.PrototypesOffset.HasValue) settings.PrototypesOffset = hook.PrototypesOffset.Value;
        if (hook.InstructionsOffset.HasValue) settings.InstructionsOffset = hook.InstructionsOffset.Value;
        if (settings.ConstantsOffset == 22172 && settings.PrototypesOffset == 98456 && settings.InstructionsOffset == 12746)
        {
            settings.DispatchMode = 3;
            settings.ProtoFormat = 3;
            settings.DecryptStrings = false;
            settings.ApplyConstantTransforms = false;
            settings.BooleanAsVarInt = false;
            settings.OperandModes = new()
            {
                [0] = OperandSemantic.Constant,
                [1] = OperandSemantic.RelForward,
                [2] = OperandSemantic.ClosureRef,
                [4] = OperandSemantic.RelBackward,
                [7] = OperandSemantic.Register,
            };
            settings.IntegerTag = 143;
            settings.FloatTag = 240;
            settings.StringTag = 149;
            settings.TwoChunk = true;
            settings.BootstrapSkipBytes = 101;
        }
        else
        {
            settings.DispatchMode = 2;
            settings.ProtoFormat = 2;
            settings.DecryptStrings = true;
            settings.ApplyConstantTransforms = true;
            settings.BooleanAsVarInt = false;
        }
        settings.TwoChunk = hook.LooksTwoChunk || settings.TwoChunk;
        settings.BootstrapSkipBytes = Math.Max(settings.BootstrapSkipBytes, 1);

        return settings;
    }

    static bool TryLoadInferredSettingsFromCache(string inputPath, string source, out LuraphSettings settings, out string cachePath)
    {
        cachePath = BuildInferredCachePath(inputPath, source);
        settings = new LuraphSettings();

        if (!File.Exists(cachePath))
            return false;

        try
        {
            settings = LuraphSettings.LoadFromJson(cachePath);
            settings.OpcodeMap ??= LuraphLifter.BuildSample2OpcodeMap();
            settings.FragmentMap ??= LuraphLifter.BuildSample2FragmentMap();
            settings.NopOpcodes ??= LuraphLifter.BuildSample2NopOpcodes();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void SaveInferredSettingsToCache(string inputPath, string source, LuraphSettings settings, out string cachePath)
    {
        cachePath = BuildInferredCachePath(inputPath, source);
        EnsureDirectory(cachePath);
        settings.SaveToJson(cachePath);
    }

    static string BuildInferredCachePath(string inputPath, string source)
    {
        var full = Path.GetFullPath(inputPath);
        var baseDir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileNameWithoutExtension(full);
        var hash = ComputeSourceHash(source);
        return Path.Combine(baseDir, ".luraph-cache", $"{fileName}.{hash}.inferred.json");
    }

    static string ComputeSourceHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
    static LuraphSettings AutoDetectBuild(string filename, string source)
    {
        var lower = filename.ToLowerInvariant();

        if (lower.Contains("sample1"))
        {
            Log("Auto-detected build: Sample1");
            return CloneSettings(KnownBuilds["Sample1"]);
        }
        if (lower.Contains("sample2"))
        {
            Log("Auto-detected build: Sample2");
            return CloneSettings(KnownBuilds["Sample2"]);
        }
        Log("No build auto-detected, defaulting to Sample2");
        return CloneSettings(KnownBuilds["Sample2"]);
    }

    static LuraphSettings CloneSettings(LuraphSettings s) => new()
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

    static (LuraphChunk? result, LuraphChunk? chunk1, string? error) DeserializeBlob(string blob, LuraphSettings settings, ParsedArgs p)
    {
        var decoder = new LuraphDecoder();
        LuraphChunk chunk;
        LuraphChunk? chunk1 = null;
        var (result, inferredSettings, autoError) = decoder.AutoDeserialize(blob, settings);
        if (result == null || inferredSettings == null)
            return (null, null, autoError ?? "All deserialization attempts failed.");

        chunk = result;
        settings = inferredSettings;
        Log($"Resolved settings: dispatch={settings.DispatchMode}, format=V{settings.ProtoFormat}, " +
            $"offsets=({settings.ConstantsOffset}, {settings.PrototypesOffset}, {settings.InstructionsOffset}), " +
            $"twoChunk={settings.TwoChunk}, skip={settings.BootstrapSkipBytes}");
        if (chunk.AntiTamperChunk != null)
        {
            chunk1 = chunk.AntiTamperChunk;
        }

        if (chunk.EntryProto == null && chunk.Prototypes.Count > 0)
        {
            if (chunk.EntryIndex >= 0 && chunk.EntryIndex < chunk.Prototypes.Count)
                chunk.EntryProto = chunk.Prototypes[chunk.EntryIndex];
            else
                chunk.EntryProto = chunk.Prototypes[0];
        }

        return (chunk, chunk1, null);
    }

    static string ExtractBlob(string source)
    {
        int longIdx = IndexOfLongStringLPH(source);
        if (longIdx >= 0)
        {
            int eqStart = longIdx + 1;
            int numEq = 0;
            while (eqStart + numEq < source.Length && source[eqStart + numEq] == '=')
                numEq++;
            int contentStart = longIdx + 1 + numEq + 1;
            string closePattern = "]" + new string('=', numEq) + "]";
            int closeIdx = source.IndexOf(closePattern, contentStart, StringComparison.Ordinal);
            if (closeIdx >= 0)
            {
                return source[contentStart..closeIdx];
            }
        }
        foreach (var prefix in new[] { "\"LPH", "'LPH" })
        {
            int qIdx = source.IndexOf(prefix, StringComparison.Ordinal);
            if (qIdx < 0) continue;

            char quote = prefix[0];
            int contentStart = qIdx + 1;
            int pos = contentStart + 3;
            int len = source.Length;

            while (pos < len)
            {
                char ch = source[pos];
                if (ch == quote)
                {
                    int bs = 0;
                    int chk = pos - 1;
                    while (chk >= 0 && source[chk] == '\\') { bs++; chk--; }

                    if (bs % 2 == 0)
                    {
                        string raw = source[contentStart..pos];
                        return UnescapeBlob(raw);
                    }
                }
                pos++;
            }
            string fallback = source[contentStart..];
            return UnescapeBlob(fallback);
        }
        int rawIdx = source.IndexOf("LPH", StringComparison.Ordinal);
        if (rawIdx >= 0)
        {
            int end = rawIdx;
            for (end = rawIdx; end < source.Length; end++)
            {
                char c = source[end];
                if (c is '"' or '\'' or ')' or '\n' or '\r')
                    break;
            }
            return source[rawIdx..end].Trim();
        }

        string largest = "";
        int i = 0;
        while (i < source.Length)
        {
            if (source[i] is '"' or '\'')
            {
                char q = source[i];
                int s = i + 1;
                i++;
                while (i < source.Length)
                {
                    if (source[i] == q)
                    {
                        int bs = 0;
                        int chk = i - 1;
                        while (chk >= s && source[chk] == '\\') { bs++; chk--; }
                        if (bs % 2 == 0) break;
                    }
                    i++;
                }
                string content = source[s..i];
                if (content.Length > largest.Length)
                    largest = content;
                if (i < source.Length) i++;
            }
            else if (i + 1 < source.Length && source[i] == '[')
            {
                int eqCount = 0;
                int j = i + 1;
                while (j < source.Length && source[j] == '=') { eqCount++; j++; }
                if (j < source.Length && source[j] == '[')
                {
                    int s = j + 1;
                    string close = "]" + new string('=', eqCount) + "]";
                    int e = source.IndexOf(close, s, StringComparison.Ordinal);
                    if (e >= 0)
                    {
                        string content = source[s..e];
                        if (content.Length > largest.Length)
                            largest = content;
                        i = e + close.Length;
                    }
                    else { i++; }
                }
                else { i++; }
            }
            else { i++; }
        }

        return largest.Length > 0 ? UnescapeBlob(largest) : source.Trim();
    }

    static int IndexOfLongStringLPH(string source)
    {
        int pos = 0;
        while (pos < source.Length)
        {
            int idx = source.IndexOf("[LPH", pos, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int bracketStart = idx;
            if (bracketStart > 0)
            {
                int chk = bracketStart - 1;
                while (chk >= 0 && source[chk] == '=') chk--;
                if (chk >= 0 && source[chk] == '[')
                    return chk;
            }
            if (bracketStart > 0 && source[bracketStart - 1] == '[')
                return bracketStart - 1;

            pos = idx + 1;
        }
        return -1;
    }

    static string UnescapeBlob(string blob)
    {
        if (string.IsNullOrEmpty(blob)) return blob;

        var sb = new StringBuilder(blob.Length);
        for (int i = 0; i < blob.Length; i++)
        {
            if (blob[i] == '\\' && i + 1 < blob.Length)
            {
                char next = blob[i + 1];
                if (next is '"' or '\'' or '\\')
                {
                    sb.Append(next);
                    i++;
                    continue;
                }
            }
            sb.Append(blob[i]);
        }
        return sb.ToString();
    }

    static string FormatIR(IRInstruction instr)
    {
        var sb = new StringBuilder();
        sb.Append($"{instr.Op,-20}");

        if (instr.Dest >= 0)
            sb.Append($" -> r{instr.Dest}");
        if (instr.OperandA != null)
            sb.Append($"  A={FormatVal(instr.OperandA)}");
        if (instr.OperandB != null)
            sb.Append($"  B={FormatVal(instr.OperandB)}");
        if (instr.OperandC != null)
            sb.Append($"  C={FormatVal(instr.OperandC)}");
        if (instr.JumpTarget >= 0)
            sb.Append($"  jmp={instr.JumpTarget}");
        if (instr.ArgCount != 0)
            sb.Append($"  args={instr.ArgCount}");
        if (instr.RetCount != 0)
            sb.Append($"  ret={instr.RetCount}");
        if (instr.Invert)
            sb.Append("  [INV]");
        if (instr.SkipNext)
            sb.Append("  [SKIP]");
        if (!string.IsNullOrEmpty(instr.Comment))
            sb.Append($"  ; {instr.Comment}");

        return sb.ToString();
    }

    static string FormatVal(IRValue v) => v.Kind switch
    {
        IRValueKind.Register => $"r{v.Index}",
        IRValueKind.Upvalue => $"up{v.Index}",
        IRValueKind.Proto => $"P{v.Index}",
        IRValueKind.Constant => v.ConstantValue switch
        {
            null => "nil",
            bool b => b ? "true" : "false",
            string s => s.Length > 30 ? $"\"{s[..27]}...\"" : $"\"{s}\"",
            double d => d.ToString("G"),
            long l => l.ToString(),
            _ => v.ConstantValue.ToString() ?? "?"
        },
        _ => "?"
    };

    static void Step(int n, int total, string msg)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"[{n}/{total}] ");
        Console.ResetColor();
        Console.WriteLine(msg);
    }

    static void Log(string msg) => Console.WriteLine($"  {msg}");

    static void Warn(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  [!] ");
        Console.ResetColor();
        Console.WriteLine(msg);
    }

    static int Error(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.Write("[ERROR] ");
        Console.ResetColor();
        Console.Error.WriteLine(msg);
        return 1;
    }

    static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
