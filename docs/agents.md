# autodock agents

autodock agents work through a repository's actionable GitHub issues on their own: one issue at a time, each change reviewed and verified, each one a pull request for you to review. You talk to them in an ordinary Claude Code terminal.

## start

1. click the agents button (bottom right of the autodock flyout, next to refresh) and pick the repository. a normal Claude Code terminal opens there, with a rainbow `autodock · agents` line above its footer that shifts every few seconds and says when agents are working, need you, are paused or off.
2. ask in plain words: *"work through the open issues; `npm test` must pass"*, *"start with #36, then #21 and #33"*.
3. confirm the autodock dialog. it shows the branch pull requests target and the exact verification command autodock will run; changing either asks again. the confirmation is remembered: agents keep going after restarts until you pause or stop them.

ask the same terminal what's going on, to reorder or skip issues, to add your own requests, or to answer a question. it reports outcomes; ask for details when you want the machinery. only its read-only tools run without asking.

right-click the agents button (or use **Agents** in the tray menu) to pause, resume or stop without opening a terminal. closing the terminal changes nothing.

## what happens

for each issue, in rank order (your order first, then urgent and bug labels, then issues other issues wait on):

1. a headless Claude Code session works on it in its own git worktree and branch, as one of your other Claude accounts.
2. autodock runs your verification command. a failure goes back to the same session.
3. a fresh read-only session reviews the change. problems go back once more.
4. autodock pushes the branch and opens a pull request (`For #36`).
5. failing checks, merge conflicts and review comments on that pull request get another pass. a merged or closed pull request finishes the item.

no new issue starts while two pull requests wait for your review; follow-ups on those always may.

when an issue needs your decision, its question is posted on the issue, autodock moves on to other work, and your reply there (or in the terminal) brings the issue back where it stopped.

autodock holds a change for you instead of pushing it when two review passes still found problems, or when it touches `.github/` (pushed workflows run with the repository's secrets). answer in the terminal to push it as is, or say what to change.

pausing or stopping also holds any push that was about to happen. closing an issue on GitHub ends its work unless a pull request is already open.

## what agents never do

- merge, close issues, deploy, publish, spend money, change settings or secrets, or make product decisions for you.
- push or call GitHub themselves. their environment has no GitHub login, no git credential helper and no network git protocol, and pushing or `gh` is denied to them; autodock pushes and opens pull requests after verification and review. sessions run as your Windows user, so this stops mistakes, not a determined program: Claude's auto-mode safety checks, the rules below and the holds above do the rest.
- take instructions from strangers. only issues you opened or that are assigned to you are picked up, and only comments from you or the repository's owners, members and collaborators count as answers or review feedback. anyone else's text is never passed on.
- use, or switch, the account you're signed in with. they run only in other accounts' isolated profiles (`cswap run N --require-session`), skip excluded and API-key accounts, stop before a subscription with extra usage on reaches the auto-switch threshold (or when its usage can't be read), and wait or move on when an account is near its limit. a session on an account you switch to is stopped and continues on another.

issues assigned to someone else, labelled as a question, discussion, blocked or on hold, waiting on another open issue (GitHub's dependencies, "blocked by #N", or the issues a `## Dependencies` section names), already covered by an open pull request, trackers (roadmaps, `[Phase N/M]` issues, checklists of issues) and decisions ("Decide …") are left alone. name one in the terminal to take it anyway.

to route agents yourself, add a section whose heading mentions autodock to an issue you wrote, with `Skip:` and `Eligible:` lines naming issues (`#1, #22–#27`). skipped issues are never picked up; when an eligible list exists, agents take up nothing else on their own, and idle when it's done.

the verification command runs the agents' code as you, with the same environment as the sessions. autodock's own git runs with hooks disabled, so nothing a session writes into the repository runs with your GitHub login.

## resilience

state lives in `%LOCALAPPDATA%\autodock\agents` (private to your user). each session runs as a native process in its own job object with files for input and output, so it keeps going if autodock exits; autodock re-attaches when it starts again, and a session ended by a restart, sign-out or shutdown continues without counting as a failure. a verification run ends with autodock and runs again on start. a session that writes nothing and uses no CPU for 15 minutes is stopped and resumed; one that keeps failing is parked with the reason.

## cost

nothing runs until you first open an agents terminal. measured on the release build:

| | |
|---|---|
| tray, agents never used | 0 ms CPU per minute, unchanged memory |
| tray, repository authorized, nothing to do | ~16 ms CPU per minute, +3 MB |
| supervisor while a session works | one wake-up per minute |
| status line | ~13 ms CPU per refresh (every 5 s while an agents terminal is open, so the label can move) |
| agents terminal's autodock tools | 35 MB while that terminal is open, no CPU when idle |

the sessions themselves are ordinary Claude Code processes, run at below-normal priority.

requires Git for Windows and the GitHub CLI (`gh auth login`), and Claude Code with auto mode.
