# loal_NAS

Repository created locally and pushed to GitHub.

## Push troubleshooting notes

This repository originally failed to push over HTTPS because outbound access to github.com:443 was unstable.

The working fix was:

1. Keep the repository remote on SSH over port 443: `ssh://git@ssh.github.com:443/wavlnm/loal_NAS.git`
2. Do not reuse the default `~/.ssh/id_rsa` key when it is already bound to another repository as a deploy key
3. Create a repository-specific SSH key for this repository
4. Add that key to GitHub as a writable deploy key
5. Configure Git to use that key with `core.sshCommand`

Current local configuration:

- Remote: `origin -> ssh://git@ssh.github.com:443/wavlnm/loal_NAS.git`
- SSH key: `C:/Users/Administrator/.ssh/id_ed25519_loal_NAS`
- Branch tracking: `master -> origin/master`

If push fails again, first verify:

- `ssh -T -p 443 git@ssh.github.com`
- `git config --get core.sshCommand`
- `git remote -v`