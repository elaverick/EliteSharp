#!/usr/bin/env bash
# Write the notes for a release to standard output: the titles of the commits
# since the previous version tag, oldest first, and a link to all the changes.
#
#   release-notes.sh <tag> [<commit>]
#
# The commit is the one being released (the tag itself if it is left out,
# which needs the tag to exist already). Run it from inside the repository,
# with all the history and tags fetched.
set -euo pipefail

tag="$1"
commit="${2:-$1}"
repository="${GITHUB_REPOSITORY:-elaverick/EliteSharp}"

# The latest version tag before this commit (none for the first release)
previous=$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$commit^" 2>/dev/null || true)

echo "## Changes"
echo
if [[ -n "$previous" ]]; then
  git log --reverse --no-merges --format='- %s (%h)' "$previous..$commit"
  echo
  echo "**Full changelog**: https://github.com/$repository/compare/$previous...$tag"
else
  git log --reverse --no-merges --format='- %s (%h)' "$commit"
  echo
  echo "**Full changelog**: https://github.com/$repository/commits/$tag"
fi
