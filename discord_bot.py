import os, sys, base64, io, re, json, asyncio, tempfile, subprocess, hashlib, zipfile, urllib.request
import discord
from discord.ext import commands

GITHUB_TOKEN = os.getenv("GH_TOKEN")
REPO = "Nestor53top/lua-deobf-actions"
BASE = os.path.dirname(os.path.abspath(__file__))

intents = discord.Intents.default()
intents.message_content = True
bot = commands.Bot(command_prefix=".", intents=intents)

OBF_INFO = {
    "auto":     "Все доступные деобфускаторы",
    "python":   "Встроенный Python-деобф (базовый: константы, операторы)",
    "white":    "White Deobfuscator JS (Prometheus/Luraph — 17 пассов)",
    "luadeobf": "Lua-Deobfuscator JS (MoonSec V2, IronBrew 2.7.0/2.7.1, AztupBrew)",
    "luraph":   "Luraph v14 (C# — только через GitHub Actions)",
    "ironbrew": "IronBrew2 (C# — только через GitHub Actions)",
    "medal":    "Medal-decompiler Rust (только через GitHub Actions)",
}

def extract_script(msg):
    if msg.attachments:
        for a in msg.attachments:
            if a.filename.endswith((".lua", ".luau", ".txt", ".luac")):
                data = urllib.request.urlopen(a.url).read()
                return a.filename, data
    if msg.content:
        m = re.search(r"```(?:lua|luau)?\s*([\s\S]*?)```", msg.content)
        if m:
            return "input.lua", m.group(1).encode()
    return None, None

def run_local_python(script_path):
    py = os.path.join(BASE, "src", "deobfuscator.py")
    out = script_path + ".deobf.py.lua"
    subprocess.run([sys.executable, py, script_path, out], capture_output=True, timeout=30)
    if os.path.exists(out):
        with open(out) as f:
            return f.read()
    return None

def run_local_white(script_path):
    white_dir = os.path.join(BASE, "src", "white")
    subprocess.run(["cp", script_path, os.path.join(white_dir, "input.lua")], capture_output=True)
    r = subprocess.run(["node", "main.js"], cwd=white_dir, capture_output=True, timeout=60)
    out_path = os.path.join(white_dir, "output.luac")
    if os.path.exists(out_path):
        with open(out_path) as f:
            return f.read()
    return r.stdout.decode() + r.stderr.decode()

def run_local_luadeobf(script_path):
    d = os.path.join(BASE, "src", "lua-deobf", "Lua-Deobfuscator")
    subprocess.run(["cp", script_path, os.path.join(d, "input.lua")], capture_output=True)
    r = subprocess.run(["node", "index.js"], cwd=d, capture_output=True, timeout=60)
    out_path = os.path.join(d, "output.luac")
    if os.path.exists(out_path):
        with open(out_path) as f:
            return f.read()
    return r.stdout.decode() + r.stderr.decode()

async def trigger_github_actions(script_b64, obfuscator, channel_id):
    data = json.dumps({
        "ref": "main",
        "inputs": {"script": script_b64, "obfuscator": obfuscator, "channel_id": str(channel_id)}
    }).encode()
    req = urllib.request.Request(
        f"https://api.github.com/repos/{REPO}/actions/workflows/deobfuscate.yml/dispatches",
        data=data,
        headers={"Authorization": f"token {GITHUB_TOKEN}", "Accept": "application/vnd.github.v3+json", "Content-Type": "application/json"},
        method="POST"
    )
    try:
        urllib.request.urlopen(req)
        return True
    except:
        return False

async def poll_github_artifact(tag):
    for _ in range(60):
        await asyncio.sleep(5)
        try:
            req = urllib.request.Request(
                f"https://api.github.com/repos/{REPO}/actions/artifacts?per_page=5",
                headers={"Authorization": f"token {GITHUB_TOKEN}", "Accept": "application/vnd.github.v3+json"}
            )
            arts = json.loads(urllib.request.urlopen(req).read()).get("artifacts", [])
            for a in arts:
                if tag in a.get("name", ""):
                    z = zipfile.ZipFile(io.BytesIO(urllib.request.urlopen(a["archive_download_url"]).read()))
                    return {n: z.read(n).decode(errors="replace") for n in z.namelist()}
        except:
            pass
    return None

LOCAL_TOOLS = {"python", "white", "luadeobf", "auto"}
REMOTE_TOOLS = {"luraph", "ironbrew", "medal"}

async def handle_deobf(ctx, obfuscator):
    await ctx.message.add_reaction("⏳")
    fname, raw = extract_script(ctx.message)
    if not raw:
        await ctx.send("❌ Прикрепи `.lua` файл или код в ```блоке```")
        return

    with tempfile.TemporaryDirectory() as tmp:
        spath = os.path.join(tmp, fname or "input.lua")
        with open(spath, "wb") as f:
            f.write(raw)

        results = {}

        if obfuscator in ("auto", "python"):
            out = run_local_python(spath)
            if out: results["python"] = out

        if obfuscator in ("auto", "white"):
            out = run_local_white(spath)
            if out: results["white"] = out

        if obfuscator in ("auto", "luadeobf"):
            out = run_local_luadeobf(spath)
            if out: results["luadeobf"] = out

        if results:
            embed = discord.Embed(title=f"✅ Деобф: {obfuscator}", color=0x00ff00)
            for tool, output in results.items():
                preview = output[:500].replace("`", "")
                embed.add_field(name=tool, value=f"```lua\n{preview}\n```", inline=False)
            await ctx.send(embed=embed)
            for tool, output in results.items():
                fpath = f"/tmp/deobfed_{tool}_{fname or 'out.lua'}"
                with open(fpath, "w") as f:
                    f.write(output[:100000])
                await ctx.send(file=discord.File(fpath))
                break

        if obfuscator in REMOTE_TOOLS or (obfuscator == "auto" and not results):
            b64 = base64.b64encode(raw).decode()
            tag = hashlib.sha256(b64.encode()).hexdigest()[:12]
            ok = await trigger_github_actions(b64, obfuscator, ctx.channel.id)
            if ok:
                await ctx.send(f"⏳ Запущен {obfuscator} на GitHub Actions...")
                res = await poll_github_artifact(tag)
                if res:
                    out = res.get("output_py.lua") or ""
                    if out:
                        fpath = f"/tmp/deobfed_gh_{fname or 'out.lua'}"
                        with open(fpath, "w") as f:
                            f.write(out[:100000])
                        await ctx.send(f"✅ Результат GitHub Actions:", file=discord.File(fpath))

@bot.event
async def on_ready():
    print(f"Bot online: {bot.user}")

@bot.command(name="ld")
async def cmd_auto(ctx): await handle_deobf(ctx, "auto")

@bot.command(name="python")
async def cmd_py(ctx): await handle_deobf(ctx, "python")

@bot.command(name="white")
async def cmd_white(ctx): await handle_deobf(ctx, "white")

@bot.command(name="luadeobf")
async def cmd_lua(ctx): await handle_deobf(ctx, "luadeobf")

@bot.command(name="luraph")
async def cmd_luraph(ctx): await handle_deobf(ctx, "luraph")

@bot.command(name="ironbrew")
async def cmd_ib2(ctx): await handle_deobf(ctx, "ironbrew")

@bot.command(name="medal")
async def cmd_medal(ctx): await handle_deobf(ctx, "medal")

@bot.command(name="help_ld")
async def cmd_help(ctx):
    e = discord.Embed(title="Lua Deobfuscator Bot", description="**Локальные** (Python/Node): сразу")
    for cmd, desc in OBF_INFO.items():
        e.add_field(name=f".{cmd}", value=desc, inline=False)
    await ctx.send(embed=e)

bot.run(os.getenv("DISCORD_BOT_TOKEN") or sys.argv[1] if len(sys.argv) > 1 else "")
