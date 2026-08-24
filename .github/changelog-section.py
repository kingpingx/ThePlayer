"""Pulls one version's section out of CHANGELOG.md, for use as GitHub release notes.

Kept as a file rather than inlined into the workflow so that it can be run by hand to check what a
release will say before the tag is pushed:

    python3 .github/changelog-section.py 0.3.0 /dev/stdout

Prints the section title to stdout and writes the body to the given path. Prints nothing and exits
0 when there is no such section - the workflow treats that as "skip", because publishing a release
with empty notes is worse than publishing none.
"""

import io
import re
import sys

HEADING = re.compile(r'^## \[(?P<version>\d+\.\d+\.\d+)\][^\S\n]*[-–—]?[^\S\n]*(?P<title>.*)$')


def main() -> int:
    if len(sys.argv) != 3:
        print('usage: changelog-section.py <version> <body-output-path>', file=sys.stderr)
        return 2

    wanted, destination = sys.argv[1], sys.argv[2]
    lines = io.open('CHANGELOG.md', encoding='utf-8').read().split('\n')

    title = None
    body: list[str] = []

    for line in lines:
        match = HEADING.match(line)

        if match:
            if title is not None:
                # The next version's heading: this section is done.
                break

            if match.group('version') == wanted:
                title = match.group('title').strip()

            continue

        if title is not None:
            body.append(line)

    if title is None:
        return 0

    text = '\n'.join(body).strip()
    io.open(destination, 'w', encoding='utf-8', newline='\n').write(text + '\n')

    print(title)
    return 0


if __name__ == '__main__':
    sys.exit(main())
