"""
In-game check of a gamemode's own Discord invite on its hub card.

    python tests/discord_invite.py <slot>

Needs a Debug build of this worktree in the slot and the game in the main menu (session.ps1 -Save 0). The Debug stub
gamemode sets DiscordInvite; the check opens the hub, reads the stub's card, clicks Open and looks for the log lines.
Prints PASS or FAIL per check and exits with the number of failures.
"""
import json
import os
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
S1 = os.path.join(ROOT, "Workspace", "tools", "bridge", "s1.py")
HUB = "sidehustle-hub"
CODE = "CKzxEgbe28"
fails = 0


def s1(slot, *args):
    p = subprocess.run([sys.executable, S1, "--slot", slot, *args], capture_output=True, text=True, timeout=120)
    if p.returncode != 0:
        print("  s1.py %s exited %d: %s" % (args[0], p.returncode, (p.stderr or p.stdout).strip()[-300:]))
    return p.stdout.strip()


def check(name, ok, detail=""):
    global fails
    print("%s %s%s" % ("PASS" if ok else "FAIL", name, (" | " + str(detail)) if detail else ""))
    if not ok:
        fails += 1


def main():
    slot = sys.argv[1] if len(sys.argv) > 1 else "mptest6"

    reg = s1(slot, "log", "--grep", "DiscordInvite: shown on its hub card", "-n", "5")
    check("the register check accepts the stub's invite", "sidehustle.debugstub" in reg, reg[-200:])

    s1(slot, "run", "shhub", "--settle-ms", "2500")
    card = s1(slot, "eval", HUB, """
        (() => {
          const d = [...document.querySelectorAll('.disc')];
          return JSON.stringify(d.map(x => ({ link: (x.querySelector('.lk') || {}).textContent || '',
                                               button: (x.querySelector('button, .btn') || {}).textContent || '' })));
        })()
    """)
    try:
        rows = json.loads(json.loads(card) if card.startswith('"') else card)
    except ValueError:
        rows = []
    check("a hub card shows the stub's Discord link", any(r["link"] == "discord.gg/" + CODE for r in rows), card[-200:])
    check("the link has an Open button", any(r["button"].strip() == "Open" for r in rows), card[-200:])

    shot = s1(slot, "shot")
    print("  screenshot: " + shot)

    # s1.py log --grep matches a plain substring, so each line gets its own query.
    before = s1(slot, "log", "--grep", "https://discord.gg/" + CODE, "-n", "50").count("\n")
    s1(slot, "click", HUB, ".disc .sm")
    time.sleep(2)
    asked = s1(slot, "log", "--grep", "opening the Discord of 'sidehustle.debugstub'", "-n", "1")
    check("Open asks for the stub's Discord", bool(asked), asked)
    opened = s1(slot, "log", "--grep", "https://discord.gg/" + CODE, "-n", "50")
    check("the link opens", opened.count("\n") > before or (before == 0 and opened), opened.splitlines()[-1] if opened else "")
    if "Steam overlay" not in (opened.splitlines()[-1] if opened else ""):
        print("  note: no Steam overlay in this game, the link went to the browser")

    errs = s1(slot, "log", "--grep", "[ERROR]", "-n", "20")
    errs = "\n".join(l for l in errs.splitlines() if "discord" in l.lower() or "hub3" in l)
    check("no Discord or hub errors in the log", not errs.strip(), errs[-200:])

    sys.exit(fails)


if __name__ == "__main__":
    main()
