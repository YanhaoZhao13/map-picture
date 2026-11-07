
## Git email privacy
All Codex, Work and other agents must use 326020963+YanhaoZhao13@users.noreply.github.com for Git author and committer identities and publishing commits in these projects.
Before committing and before pushing, check `git config user.email` and `git log -1 --format='%an <%ae> / %cn <%ce>'`. Inspect the prospective commit's author and committer too. If a private email is found, stop, repair it and verify again. Never push private email metadata. Never rewrite remote history without explicit user approval.
