// Patch Goblins' goblin where Claude Code draws its mascot, in the agents terminal only: loaded into that one
// session by BUN_OPTIONS=--preload, never installed anywhere. Claude Code (a Bun executable) draws the mascot as
// three rows of fixed glyph runs painted in the theme's `clawd_body`; this swaps each run for a goblin run of the
// same width on its way to stdout and keeps every escape sequence (colors, backgrounds) in place. Rows it does
// not recognise (the entrance poses, other terminals' variants) pass through unchanged, so nothing else is touched.
"use strict";
const SGR = "((?:\\x1b\\[[0-9;]*m)*)"; // what Claude puts between two runs: kept as a group
const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const RUNS = [
  // Claude's runs (default pose, 9 cells; row 0 has 8) -> the goblin's, cell for cell
  [[" ▐", "▛███▛█"], ["▐▖", "▄▄▄▄▗▌"]],
  [["▝▜", "█████", "█▀"], ["▝█", "▄██▄█", "▘ "]],
  [[" ▝▝   ▝▝ "], [" ▝▟▟▙▙▘  "]],
];
const RULES = RUNS.map(([from, to]) => [
  new RegExp(from.map(esc).join(SGR), "g"),
  (...m) => to.map((run, i) => run + (i < from.length - 1 ? m[i + 1] : "")).join(""),
]);
const swap = (s) => RULES.reduce((t, [re, to]) => t.replace(re, to), s);

const out = process.stdout;
const write = out.write.bind(out);
out.write = (chunk, ...rest) => write(typeof chunk === "string" && chunk.includes("█") ? swap(chunk) : chunk, ...rest);

module.exports = { swap }; // for the test
