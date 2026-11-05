# Ignore rule fixtures

Real rule files for `RepositoryFileSelectorDifferentialTests`.

- `visual-studio.rules` is GitHub's `VisualStudio.gitignore` from
  [github/gitignore](https://github.com/github/gitignore), CC0-1.0.
- `code-context.rules` is this repository's `.gitignore`.
- `d2-roguelike.rules` is the `.gitignore` of Ben Parker's d2-roguelike repository.

The gate pins agreement with the regular expression selector this replaced, not with Git.
Where that selector differed from Git, the difference is kept: `x[!a]b` matches `x/b`, for
one. A comparison with `git check-ignore` is a follow-up.
