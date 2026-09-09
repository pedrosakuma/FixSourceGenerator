# Local issue execution

Use this runner instead of assigning issues to the remote/cloud Copilot coding agent.
It requires Bash, Git, `gh`, Copilot CLI and `flock`. Authenticate the CLIs separately.
The runner never commits, pushes, publishes, or changes issue state.

## First wave

Start from a reviewed, committed and published baseline. Prepare both tasks from the same
explicit commit (not a moving branch name recorded only in a prompt):

```bash
scripts/agents/local.sh prepare 29 HEAD claude-sonnet-5
scripts/agents/local.sh prepare 30 HEAD gpt-5.6-sol
scripts/agents/local.sh run 29
scripts/agents/local.sh run 30
```

`prepare` resolves the ref to a commit, snapshots the issue body, and creates a new
`agents/issue-N` branch/worktree under the repository's Git common directory. It refuses
a dirty baseline, an existing state directory/branch or an open prerequisite issue.
For #31-#34 the roadmap prerequisites are checked; the integrator must additionally make
sure the chosen base actually contains the integrated prerequisite commits.

`run` is a foreground command. Use separate terminals or the supervising CLI's attached
background-command facility to run independent tasks concurrently. There is no detach,
automatic restart, cleanup or merge. State, prompts, output and usage logs stay under
the Git common directory, not in tracked files:

```bash
scripts/agents/local.sh status 29
scripts/agents/local.sh status 30
```

An advisory per-issue lock prevents two executors from sharing one worktree. `needs-review`
means the CLI exited successfully, not that the task has passed review or is ready to merge.
Inspect output and changes before committing. Failed/interrupted runs are preserved; do not
blindly delete state or rerun against partial work.

After inspecting a `needs-review` delivery, the integrator can explicitly request a revision
using the completed session UUID printed at the end of its output and a feedback file:

```bash
scripts/agents/local.sh revise 29 SESSION_UUID /absolute/review-feedback.txt gpt-5.6-sol
```

This resumes the local conversation with the existing uncommitted changes, optionally on a
different model. Original output is preserved and revision logs get their own directory.
The pinned HEAD must still match: revision is for an uncommitted delivery, not for silently
rebasing or rewriting an already integrated branch. Failed/interrupted sessions are not
automatically resumed. The same permission restrictions and per-issue lock apply.

## Permissions and integration

The CLI retains its normal path/URL checks. Tools are allowlisted for local editing,
read-only Git inspection and .NET work; remote GitHub MCP access is disabled. This is an
operational safeguard, not a filesystem/network sandbox: build commands execute repository
code. Only run reviewed/trusted baselines and do not broaden permissions to bypass denials.

Set `NUGET_PACKAGES` outside the script if a dedicated cache is needed. Fresh worktrees may
need the existing README/CI dependency setup for the local dotnet-diagnostics feed; the
runner does not install tools or download packages automatically.

The integrator owns shared runtime merges, review with a model different from the
implementer, commits (including the Copilot co-author trailer), PRs and issue updates.
Do not benchmark concurrently with other builds/loads. Reader/writer work starts only
after the API contract is agreed; shared RuntimeGenerator changes are integrated serially.
