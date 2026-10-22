"""Writes the README's SVG art from the goblin sprite in make-icon.py: assets/readme/hero.svg and workflow.svg.
Run: python dock/tools/make-readme-art.py"""
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("icon", Path(__file__).with_name("make-icon.py"))
icon = importlib.util.module_from_spec(spec)
spec.loader.exec_module(icon)

BG0, BG1 = "#0d1810", "#16281a"
FG, MUTED, GREEN, YELLOW, AMBER, GREY = "#eef4e6", "#8fa98f", "#8dd258", "#ffe24d", "#e3a33b", "#6b7a6b"
SANS = "'Segoe UI Variable Display','Segoe UI',-apple-system,BlinkMacSystemFont,'Helvetica Neue',Arial,sans-serif"
MONO = "'Cascadia Mono',Consolas,'SFMono-Regular',Menlo,monospace"


def goblin(x, y, px, rows=icon.SPRITE):
    """The sprite as merged runs of rects (one per same-colour run in a row) at (x, y), px per pixel."""
    out = []
    for r, row in enumerate(rows):
        c = 0
        while c < 16:
            ch, end = row[c], c
            while end < 16 and row[end] == ch:
                end += 1
            if icon.PALETTE[ch]:
                out.append(f'<rect x="{x + c * px}" y="{y + r * px}" width="{(end - c) * px}" height="{px}" fill="#%02x%02x%02x"/>' % icon.PALETTE[ch])
            c = end
    return "\n".join(out)


def svg(w, h, title, desc, body):
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" role="img" aria-labelledby="t d">
<title id="t">{title}</title>
<desc id="d">{desc}</desc>
<defs>
<linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{BG0}"/><stop offset="1" stop-color="{BG1}"/></linearGradient>
<pattern id="px" width="24" height="24" patternUnits="userSpaceOnUse"><rect x="11" y="11" width="2" height="2" fill="{GREEN}" opacity="0.07"/></pattern>
</defs>
<rect width="{w}" height="{h}" rx="20" fill="url(#bg)"/>
<rect width="{w}" height="{h}" rx="20" fill="url(#px)"/>
{body}
</svg>
"""


def card(x, y, name, dot, job):
    """One team member: a small goblin, its name, and the job line with the flyout's coloured dot."""
    return f"""<g>
<rect x="{x}" y="{y}" width="252" height="92" rx="12" fill="#0a130c" stroke="#24402a"/>
{goblin(x + 16, y + 22, 3)}
<text x="{x + 80}" y="{y + 40}" font-family="{SANS}" font-size="22" font-weight="600" fill="{FG}">{name}</text>
<rect x="{x + 80}" y="{y + 56}" width="8" height="8" fill="{dot}"/>
<text x="{x + 96}" y="{y + 65}" font-family="{MONO}" font-size="18" fill="{MUTED}">{job}</text>
</g>"""


def hero(tagline, sub):
    cards = [("you", GREEN, "terminal open"), ("Snag", GREEN, "#42 · reviewing"),
             ("Grub", GREEN, "#57 · working"), ("Mossy", AMBER, "resting · 1h")]
    body = goblin(64, 52, 15) + f"""
<text x="344" y="118" font-family="{SANS}" font-size="84" font-weight="700" fill="{GREEN}" letter-spacing="-1">Patch Goblins</text>
<text x="348" y="172" font-family="{SANS}" font-size="32" fill="{FG}">{tagline}</text>
<text x="348" y="214" font-family="{MONO}" font-size="20" fill="{MUTED}">{sub}</text>
""" + "\n".join(card(64 + i * 272, 300, *c) for i, c in enumerate(cards))
    return svg(1200, 420, "Patch Goblins: " + tagline,
               "A green pixel-art goblin beside the name, and four goblins of a team: one is yours, two are building pull requests, one is resting until its quota resets.", body)


def workflow():
    steps = [("1", "you", "set a goal or", "point at issues"), ("2", "Director", "plans steps,", "picks model + effort"),
             ("3", "goblins", "build, one worktree", "per spare account"), ("4", "checks", "your tests, then", "a fresh review"),
             ("5", "pull request", "opened for you;", "you merge")]
    parts, x = [], 40
    for i, (n, head, l1, l2) in enumerate(steps):
        hl = i == 4
        parts.append(f"""<rect x="{x}" y="40" width="200" height="150" rx="12" fill="{'#183a1d' if hl else '#0a130c'}" stroke="{GREEN if hl else '#24402a'}"/>
<rect x="{x + 18}" y="58" width="30" height="30" fill="{YELLOW if hl else GREEN}"/>
<text x="{x + 33}" y="80" text-anchor="middle" font-family="{MONO}" font-size="20" font-weight="700" fill="#0d1810">{n}</text>
<text x="{x + 18}" y="122" font-family="{SANS}" font-size="24" font-weight="600" fill="{FG}">{head}</text>
<text x="{x + 18}" y="150" font-family="{SANS}" font-size="18" fill="{MUTED}">{l1}</text>
<text x="{x + 18}" y="173" font-family="{SANS}" font-size="18" fill="{MUTED}">{l2}</text>""")
        if i < 4:
            parts.append(f'<g fill="{GREEN}"><rect x="{x + 208}" y="111" width="12" height="8"/><rect x="{x + 220}" y="107" width="4" height="16"/><rect x="{x + 224}" y="111" width="4" height="8"/></g>')
        x += 228
    return svg(1200, 230, "How Patch Goblins works",
               "Five steps: you set a goal, the Director plans, goblins build in worktrees on spare accounts, your tests and a fresh review check the change, and a pull request waits for you to merge.",
               "\n".join(parts))


if __name__ == "__main__":
    out = ROOT / "assets" / "readme"
    out.mkdir(parents=True, exist_ok=True)
    (out / "hero.svg").write_text(hero("Your Claude accounts. One coding team.",
                                       "goals in · reviewed pull requests out · Windows 11"), encoding="utf-8")
    (out / "workflow.svg").write_text(workflow(), encoding="utf-8")
    print("wrote", *sorted(p.name for p in out.glob("*.svg")))
