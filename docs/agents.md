# Patch Goblins agents

Patch Goblins agents work through a repository's actionable GitHub issues on their own: one issue at a time by default (more if you set it), each change reviewed and verified, each one a pull request for you to review. You talk to them in an ordinary Claude Code terminal.

## start

1. click the agents button (bottom right of the Patch Goblins flyout, next to refresh) and pick the repository. a normal Claude Code terminal opens there, with a green `Patch Goblins` line above its footer that shifts every few seconds and says when agents need you, are paused or off.
2. ask in plain words: *"work through the open issues; `npm test` must pass"*, *"start with #36, then #21 and #33"*.
3. confirm the Patch Goblins dialog. it shows the branch pull requests target and the exact verification command Patch Goblins will run; changing either asks again. the confirmation is remembered, but agents only work while that repository's agents terminal is open: close it and they pause where they are, reopen it and they continue. nothing runs in the background.

ask the same terminal what's going on, to reorder or skip issues, to add your own requests, or to answer a question. it reports outcomes; ask for details when you want the machinery. only its read-only tools run without asking.

right-click the agents button (or use **Agents** in the tray menu) to pause or stop. the agents button gets a yellow dot only when something is broken that you need to fix (repeated failures, a push or pull request error, gh not signed in, a failing verification command, no usable account); questions and approvals wait in the terminal.

## what happens

for each issue, in rank order (your order first, then urgent and bug labels, then issues other issues wait on):

1. a headless Claude Code session works on it in its own git worktree and branch, as one of your other Claude accounts.
2. Patch Goblins runs your verification command. a failure goes back to the same session.
3. a fresh read-only session reviews the change. problems go back once more.
4. Patch Goblins pushes the branch and opens a pull request (`For #36`).
5. failing checks, merge conflicts and review comments on that pull request get another pass. a merged or closed pull request finishes the item.

no new issue starts while ten pull requests wait for your review (**Open pull requests** in settings, 1–50); follow-ups on those always may. **Draft pull requests** opens them as drafts.

**Issues at once** in settings sets how many issues are worked at the same time (default 1). 0 is automatic: one per account with quota to spare, started a few minutes apart. each runs in its own worktree on its own account.

when an issue needs your decision, its question is posted on the issue, Patch Goblins moves on to other work, and your reply there (or in the terminal) brings the issue back where it stopped.

Patch Goblins holds a change for you instead of pushing it when two review passes still found problems, or when it touches `.github/` (pushed workflows run with the repository's secrets). answer in the terminal to push it as is, or say what to change.

pausing or stopping also holds any push that was about to happen. closing an issue on GitHub ends its work unless a pull request is already open.

## goals

for an objective bigger than one change, set a goal in the terminal: *"goal: the app has a settings page; `npm test` must pass"*. a read-only Director session on Opus reads the whole project (it changes nothing, and only goes to an account with Opus weekly room left) and plans the next few steps, each one a pull request like any other; steps wait for the ones they build on to be merged. it plans again as steps land, and asks you when only you can decide. ask how goals stand, answer a goal's question, ask for a new plan, drop a goal, or say it's done, all in the terminal.

a goal counts as achieved only on evidence: once its steps are done, Patch Goblins runs the checks you gave it (you confirm the exact commands in a Patch Goblins dialog) and the verification command in a fresh checkout of the base branch. a claim without a check or merged work is put to you instead. three plans in a row that change nothing, or three failed plans, park the goal for you.

a goal can plan steps in your other repositories that agents work on (by folder name), and a step in one can wait for a step in another. those steps run under that repository's own rules: only while its agents terminal is open, with its verification command and its pull request limit. the Director only plans new steps there; issues there are taken up by that repository's own agents.

## models, effort and ultracode

every run states three separate settings: the model (Haiku, Sonnet or Opus; a run's subagents use its model, and no advisor consults another), the effort (low to max) and ultracode (on or off). none implies another.

- **who decides.** the Director recommends them, with a difficulty (trivial, standard, hard), for each step it plans and for the queued work. issues with no goal get the same: when queued work near the front has no route yet, a short read-only director pass on Sonnet orders it, routes it, and may say which issue must land before another (a later pass can add such an order, never drop one). it plans no new work, shows up in no goal list, and an issue waits for it only while it runs. Patch Goblins checks every recommendation: Haiku only for trivial work, unknown values fall back by difficulty, reviewers are never Haiku. without a recommendation a run uses Sonnet at medium effort.
- **quota.** a run goes to an account with room in that model's own weekly window. with no Opus room anywhere, Opus work runs on Sonnet at no less than high effort instead of waiting, unless failures already moved it to Opus.
- **ultracode** is off unless you turn on **Ultracode** in settings. then a run gets it when the Director (or an escalation) asks for it, whatever its model or effort, and only on an account that runs nothing else and is at least 30 points below the switch threshold. it is never used for reviews or the Director.
- **failures.** a review that finds problems moves the fix to a stronger model; verification or pull request checks that fail a second time raise the effort; once both are at the top, ultracode (if allowed). limits, stalls, crashes and a single failing check are not escalated. the worktree, branch and conversation stay.
- **measured.** each finished run adds a line to `metrics.jsonl` in the agents folder: its settings, the model Claude reports, duration, tokens (subagents included), its account's usage at start and after the end, and the outcome. the end reading is the tray's next refresh after the run; it is left out, with the reason, when another run started on that account first or a usage window reset meanwhile. quota is read from the usage windows, never from the API-equivalent cost Claude prints. `agents_details` shows an item's route and its last run.

a step that waits on another gets a short summary of what that one did.

## memory

agents share short notes: what you told them (answers, or *"remember: we never touch the v1 API"*), and what runs learned (merged and closed pull requests, facts a run or the Director wrote down). your notes bind every agent; theirs are hints. notes are per repository, or for every repository when you say so. ask the terminal to list or forget them.

## what agents never do

- merge, close issues, deploy, publish, spend money, change settings or secrets, or make product decisions for you.
- push or call GitHub themselves. their environment has no GitHub login, no git credential helper and no network git protocol, and pushing or `gh` is denied to them; Patch Goblins pushes and opens pull requests after verification and review. sessions run as your Windows user, so this stops mistakes, not a determined program: Claude's auto-mode safety checks, the rules below and the holds above do the rest.
- take instructions from strangers. only issues you opened or that are assigned to you are picked up, and only comments from you or the repository's owners, members and collaborators count as answers or review feedback. anyone else's text is never passed on.
- use, or switch, the account you're signed in with. they run only in other accounts' isolated profiles (`cswap run N --require-session`), skip excluded and API-key accounts, stop before a subscription with extra usage on reaches the auto-switch threshold (or when its usage can't be read), and wait or move on when an account is near its limit. a session on an account you switch to is stopped and continues on another. when your own login reaches the auto-switch threshold and the only accounts with room are ones agents run on, the agents give one up for half an hour (their sessions stop and continue on another account) so your login can switch there.
  - **one account only.** with a single Claude account there is nothing to switch to, so agents share your login instead of waiting for a second account: plain Claude Code on it, never a second copy of its credentials, with the same restrictions as every run. they start only while none of your own Claude terminals is working (agents terminals don't count) and while the account has room, otherwise the work queues; a session of yours is never closed or moved, and a run already going is not interrupted when you start one. no ultracode there. adding a second account ends this: runs on the login stop and continue on the new account.

issues assigned to someone else, labelled as a question, discussion, blocked or on hold, waiting on another open issue (GitHub's dependencies, "blocked by #N", or the issues a `## Dependencies` section names), already covered by an open pull request, trackers (roadmaps, `[Phase N/M]` issues, checklists of issues) and decisions ("Decide …") are left alone. name one in the terminal to take it anyway.

to route agents yourself, add a section whose heading mentions Patch Goblins to an issue you wrote, with `Skip:` and `Eligible:` lines naming issues (`#1, #22–#27`). skipped issues are never picked up; when an eligible list exists, agents take up nothing else on their own, and idle when it's done.

the verification command runs the agents' code as you, with the same environment as the sessions. Patch Goblins' own git runs with hooks disabled, so nothing a session writes into the repository runs with your GitHub login.

## resilience

state lives in `%LOCALAPPDATA%\patch-goblins\agents` (private to your user). each session runs as a native process in its own job object with files for input and output, so a restart of Patch Goblins doesn't lose it: Patch Goblins re-attaches when it starts again and lets it carry on if its terminal is still open (otherwise it stops, as a pause). a session ended by a restart, sign-out or shutdown continues without counting as a failure. a verification run ends with Patch Goblins, or with the terminal, and runs again later. a session that streams nothing and uses no CPU for 10 minutes is stopped and resumed (25 minutes while a command, subagent or background task it started is still running, or with ultracode); thinking and writing stream, so a session that is reasoning is never taken for stuck. one that gave its final answer but does not exit is closed within a minute and judged as finished. one that keeps failing is parked with the reason.

when every account is near its limit, nothing new starts: Patch Goblins says so once, with the earliest reset, and the agents continue by themselves when an account has usage again. with **Auto-Prime** on, fresh 5-hour windows are started early while agents run, so they also reset early. once a day, from 8:00, a notification sums up what the agents did since the last one.

## cost

nothing runs until you first open an agents terminal. measured on the release build:

| | |
|---|---|
| tray, agents never used | 0 ms CPU per minute, unchanged memory |
| tray, repository authorized, nothing to do | ~16 ms CPU per minute, +3 MB |
| supervisor while a session works | one wake-up per minute |
| status line | ~13 ms CPU per refresh (every 5 s while an agents terminal is open, so the label can move) |
| agents terminal's Patch Goblins tools | 35 MB while that terminal is open, no CPU when idle |

the sessions themselves are ordinary Claude Code processes, run at below-normal priority.

requires Git for Windows and the GitHub CLI (`gh auth login`), and Claude Code with auto mode.
