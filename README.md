# JSON Toolbox

A desktop toolbox for JSON files that are too large to open and too messy to trust.

Two things set it apart from the viewers that already exist. It is built on a **streaming
backend**, so a multi-gigabyte file opens instantly and is browsed without ever being held in
memory. And it **inspects** rather than merely validates: alongside syntax errors it reports
the values that are perfectly legal JSON and will still be misread downstream.

## What it finds

Syntax errors are the easy part, and the toolbox tries to explain them rather than repeat
the parser's complaint — a trailing comma is reported as a trailing comma, not as an
unexpected `}` several characters later. The same goes for apostrophe-quoted strings,
comments, `NaN` and `Infinity`, and unquoted keys.

The findings worth having are the ones a validator will not give you:

| Code | Finding |
| --- | --- |
| `JE0002` | Duplicate key in one object — parsers disagree about which value wins |
| `JE0003` | Integer beyond 2^53, which every consumer decoding JSON numbers as doubles will read differently |
| `JE0004` | Floating point value carrying more digits than a double can hold |
| `JE0005` | A path that is not consistently typed — `price` is a number 8 831 253 times and a string 177 times |
| `JE0006` | One object mixing `camelCase` and `snake_case` keys |
| `JE0007` | Keys that differ only by letter case, which case-insensitive consumers will merge |
| `JE0008` | `"null"`, `"undefined"` and friends written as text, where no null check will find them |
| `JE0011` | A field present in 999 of 1 000 records — optional by accident, not by design |
| `JE0012` | A property that looks like it holds a credential |
| `JE0013` | Double-encoded JSON: a string whose content is itself a document |

Findings are folded by rule and shape, so a million records with the same defect produce one
row with a count rather than a million rows.

Inspection also builds a **structure profile**: the schema inferred from the data, with the
number of occurrences and the share of bytes for every path. It answers "what is actually in
this file" and "why is it so large" without a query language.

## Architecture

```
JsonToolbox.Core          parsing, indexing, search, inspection — no UI dependency
JsonToolbox.App           Avalonia desktop application
  Styles/Tokens.axaml     every colour, once per theme
  Styles/Controls.axaml   appearance only, on top of Fluent
  Assets/icon.ico         the application icon, drawn by the script below
JsonToolbox.Core.Tests
JsonToolbox.App.Tests     the command line, and whatever else is logic rather than layout
tools/generate-icon.ps1   redraws the icon at every size it needs
```

Three decisions shape everything else.

**One scanner, many visitors.** `JsonScanner` reads a document in a single forward pass over
a rolling buffer and hands each token to a `JsonScanVisitor`. Indexing, searching and
inspecting are all visitors over that one scan, so no feature needs its own parser and none of
them need the document to fit in RAM.

**Index one level at a time.** Opening a file looks at its first non-whitespace byte and stops
there. A node's children are read the first time somebody expands it, from a memory-mapped
slice covering exactly that node. Opening a gigabyte costs about three milliseconds.

**Announce containers when they open.** A child is reported at its opening bracket, before its
size is known, and updated when its closing bracket is reached. Without this, the last child
of a large document would only appear once the whole file had been read — which is precisely
the wait the design exists to avoid.

**Index in full, show a page.** Reading fifty thousand children of a large array costs a few
hundred milliseconds on a worker thread; turning them into fifty thousand rows costs seconds
of frozen window, and nobody scrolls through fifty thousand rows. So the index is kept as
plain structs and the rows are materialised five hundred at a time, in one batched change
rather than one change per row.

**Show the tree as a flat list.** A nested tree control has to guess how tall the branches it
has not built are, and it guesses by averaging the rows it has built. Expanding one node
breaks that average — a row twelve times taller than its neighbours drags it up by a third —
and the control then believes it is scrolled somewhere else entirely: opening item 264 of a
long array left you looking at item 182. Flattening removes the guess. Every row is one line
tall, the list knows exactly how many there are, expanding inserts rows into the middle
without changing anything above them, and indentation is drawn from each row's depth.

**Diff by structure, shown side by side.** Comparing two documents as text reports reformatting
and reordered keys as changes, and misses that a record moved. `Compare` matches objects by
key, and array elements by their identifier where the records carry one — so an edited record
reads as "this field changed", not as one record leaving and another arriving. What makes it
affordable is that identical bytes mean an identical subtree: an unchanged branch is dismissed
without being parsed, and a changed one fails that comparison within a few bytes. One changed
value in a pair of 300 MB files is found in 1.2 seconds.

A comparison opens as a tab beside the open documents rather than as a mode to be entered and
left, so switching is a click on the thing you want (or Ctrl+Tab) instead of dismissing the
thing you do not.

Both sides are chosen in that tab rather than assumed. `Compare` used to mean "this document
against a file you are about to pick", which is one of the several things a person might want;
now each side is a picker offering the documents that are already open — with their unsaved
edits, so what is compared is what is on screen — and a button for any other file on disk.
Either side can be changed afterwards without starting again, and the comparison re-runs as
soon as both are set. Closing a document a side points at clears that side and the result with
it, rather than leaving a comparison of something that is no longer there.

The result is shown as two panes, and they are one list: each row is a pair, drawing its left
and right halves in two columns. Alignment is therefore not something to maintain and there is
no scrolling to keep in step. Rows are tinted by what happened to them, "changes only" hides
what the two documents agree on, and the pairing that draws the panes is the same code that
produces the list of findings — so the two views cannot disagree.

Inside a changed value the edit itself is marked: green on the right for what arrived, red on
the left for what went. Two values side by side answer "did this change"; they do not answer
"what changed", and for `"Sahakar Nagar"` against `"Sahakar Nagars"` that is one letter nobody
should have to hunt for.

**One search box, for whatever is on screen.** A comparison of two large documents answers "what
changed" with hundreds of rows, and the question that follows is always narrower — what changed
about the price, about this record, about anything called `id`. So the toolbar's search narrows
the comparison when the comparison is what you are looking at, and searches the file when a
document is. The two are arrived at differently — one reads the whole file, the other filters
differences already found and needs no Find button, so the modifiers and the button step aside —
but that is a difference in how the answer is reached, not in what is being asked, and it does
not justify a second field somewhere else on the screen. Each view keeps its own text, so
switching tabs brings back what was being looked for there.

Narrowing the comparison drives both of its views from the one text, for the same reason they
share their pairing: the panes and the list of findings cannot be allowed to disagree about what
is in the comparison. A container is kept while the rows are built, because whether anything
inside it matches is not known until it has been opened; once opened, one that holds nothing
matching is a heading over nothing and goes.

**Read what is there, say what is wrong with it.** Configuration files written by hand are full
of comments and trailing commas, and a reader that refuses them leaves somebody staring at a
document they can plainly see is there. So the first refusal switches the tree to reading
leniently, once, for the whole document, and says so — in the status bar and on a chip in the
panel header, because it changes what the rows below mean. Inspection stays strict: reporting
that the file is not portable JSON, and explaining that a comment is a comment rather than an
unexpected `/`, is exactly its job.

A document that is broken beyond that opens the branch onto the reason, at the line and column
where it stops making sense, and the rest of the tree stays usable. Nothing a file contains is
allowed to end the process: a syntax error is a thing to be told about, not a thing to fall over,
and a bug that gets past the operation that should have reported it costs a message rather than
the document somebody had open with unsaved work in it.

**Sort properties without touching the file.** A record whose forty keys arrive in whatever
order a serialiser emitted them is read by hunting, and the fix is usually to look at it
differently rather than to rewrite it. `A→Z` and `Z→A` in the panel header reorder what the tree
lists; the document keeps the order it was written in. Array elements are never reordered, in
the view or otherwise: an element is identified by its position, so moving one would not be a
different view of the same data — it would be different data, and every index pointing into it
would be wrong.

When the order should be in the file too, `Apply order` writes it there as an ordinary edit —
one step of undo however many objects it reached, and nothing on disk until Save. The rewrite is
a permutation of bytes the document already holds: each member is copied with the exact name,
colon and value it was written with, so numbers keep their digits and strings keep their
escapes. What stays still is the separators — the comma, the newline, the indentation of the
next line — because they belong to the position and not to the member that moves into it. Carry
each member's trailing comma with it instead and the member that ends up last takes a comma it
should not have, which is a document that no longer parses. Right-clicking a container sorts
just that one.

**One session per file.** Everything that belongs to a document — its tree, its search, its
findings, its pending edits — lives in that document's own session, so opening a second file
opens a second session rather than swapping the contents of one. Switching tabs changes which
session is on screen and nothing else: neither loses its place, its search results, or its
unsaved work, and a modified tab carries the same mark the window title does.

**Edit without rewriting the file.** The whole design rests on values being byte offsets into a
file that is never copied, and editing breaks that: change one character near the front and
every offset behind it moves. So an edited document is a **piece table** — an ordered list of
slices of the untouched original and of an append-only buffer of everything typed since. An
edit splits at most two pieces and inserts one, which costs the same whether the file is a
kilobyte or a gigabyte, and undo is simply the previous list.

That change reached nothing else. Scanning, indexing, searching, inspecting and comparing all
read their document through `JsonSource` and ask only for a length and some bytes at an offset,
which a piece table can answer; so the same code walks an edited document as walks a file on
disk and cannot tell the difference.

A value is replaced by typing JSON into the value pane, which is parsed before it is accepted;
keys are renamed, members added, and members deleted along with the comma that joined them to
their neighbours. Saving streams the pieces to a file beside the original and moves it into
place, so an interrupted save leaves the original whole.

**Pin a key, read it on the parent.** Right-clicking a property pins its name, and every
container that has a property by that name shows its value on its own row — so an array of
records can be scanned without opening any of them. The values cost nothing to collect: the
scan that lists a container's children is already inside each child, one level down, counting
its contents, so it picks up the pinned names on the way past.

The interface follows from the same idea. Every colour is a named token defined once per theme
in `Styles/Tokens.axaml`, so light and dark are not two designs; values are written the way
they appear in the document, quotes and all, because the difference between `2006` and
`"2006"` is exactly what this application exists to make visible; and each panel says what it
would show rather than sitting empty until something has been run.

## Measured

A 1.0 GB document of 8.8 million records, on an ordinary desktop:

| Operation | Time |
| --- | --- |
| Open the file | 3 ms |
| Announce the top-level children | 11 ms |
| Expand a record 500 MB into the file | 0.2 ms |
| Full-text search across the whole document | 4.3 s (238 MB/s) |
| Full inspection: 82 M values, all rules | 11.8 s (87 MB/s) |

Live managed memory stayed at 6.7 MB throughout.

Responsiveness is measured separately, by asking the window to answer a message while a
document loads. Opening a 300 MB minified array of 8.8 million values, the longest the UI
thread was blocked was **10–21 ms**, against 51 ms for the application sitting idle.

## Running it

```bash
dotnet run --project JsonToolbox.App
```

Drop a file on the window, use `Open…`, or pass paths on the command line — every one of them
opens, so the toolbox can serve as the "Open with" handler for `.json`. `Inspect` runs the full
analysis; the search box covers keys, values, or both, with regular expressions and whole-word
matching.

```bash
JsonToolbox before.json after.json --compare
```

`--help` prints the usage and exits without opening a window. It writes to the console the
command was typed in: a windowed program starts without one on Windows, so it borrows its
parent's — which means the text lands under the prompt the shell has already printed back,
the way it does for every windowed program that does this.

`--compare` (or `-c`, or `--diff`) opens the two files given and lands on the comparison with
the diff already run, so a script, a shell alias or a version-control difftool goes from two
paths to a side-by-side view without a click. Both files are open as tabs as well, and either
side can be re-pointed at something else afterwards, because it is the same comparison the
button opens rather than a separate read-only mode. A command line that does not add up — one
file, an unknown switch, a path that is not there — opens the window and says so in the status
bar rather than failing silently or refusing to start.

```bash
dotnet test
```

## Releasing

Pushing a version tag builds the release and publishes it:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

`.github/workflows/release.yml` restores, builds, runs both test projects, and publishes a
self-contained single-file `win-x64` build, so the download runs on a machine with no .NET on
it. The archive goes to a GitHub release along with its SHA-256, cut with the `gh` that is on
the runner rather than a third-party action that would have to be trusted with the token.

The tag has to agree with `VersionPrefix` in `Directory.Build.props`, and the run fails if it
does not. The application shows its version in the status bar and reads it from the assembly it
was built into, so a tag saying one thing while the build says another would produce a release
nobody could identify afterwards. Bumping that one property is what makes a release.

Running the workflow by hand builds and tests everything and keeps the archive as an artifact
without publishing anything, which is how to check a change to the workflow itself.

## Not done yet

The foundation is complete and covered by tests; these are the next features rather than known
defects.

- Synchronized raw text view alongside the tree, with the search term marked up in it too
  (it is already marked up in the tree, the results list and the value pane)
- Table view for arrays of like-shaped objects
- JSONPath and JMESPath queries, beyond the current substring and regex search
- Three-way merge, and exporting a diff as a patch
- JSON Schema validation against a supplied schema
- Decoding of embedded formats in place: base64, JWT, timestamps, URL encoding
- Redaction mode for sharing, and type generation for C# and TypeScript
- NDJSON / JSON Lines support
- Keeping the undo history across a save. Saving in place means releasing the mapping the
  document is read through and opening the new file, which the recorded snapshots cannot
  survive; keeping them would mean holding the old file open until the application closes.
- Editing values too large to show as text, and reordering members
- Keeping each tab's scroll position. Which nodes are open survives a switch because it lives
  in the session, but the view is rebuilt when a tab is shown, so it returns to the top.
- Reordering tabs, and reopening a document that was closed by mistake
