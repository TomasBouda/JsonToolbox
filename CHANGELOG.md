# Changelog

What changed for the user, newest version first. The version is the one shown in the
window's header and read back from the build.

## 0.1.1 — 2026-09-24

### Added
- **Text panel**: the file as it is written, beside the tree. Selecting in the tree marks the
  value in the text and scrolls to it; clicking in the text selects the value in the tree; the
  search term is marked up there too. Works on files of any size — a minified gigabyte is shown
  as rows without being loaded.
- **Table** panel sorts by column (numbers as numbers, then back to file order) and filters
  the records by any text.
- **JSON Lines** and other sequences of documents open as a list of their records.
- **Double-encoded JSON** — a document serialised into a string — is confirmed by parsing it,
  and can be shown decoded with the `decoded` chip in the value pane.
- **Command palette** (Ctrl+K): fuzzy search across open documents, panels and actions.
- Tabs remember their scroll position, can be dragged into a different order, close with
  Ctrl+W or a middle click, and the one closed last comes back with Ctrl+Shift+T.

### Changed
- New look, shared with the other desktop tools: dark-first, hairline borders, one accent, the
  header doubling as the title bar with the version in it, a status bar, and a keyboard-hint
  footer. Light and dark are both deliberate palettes; the choice is remembered between starts
  (Ctrl+Shift+L toggles it).
- Theme switch System / Light / Dark: the header button and Ctrl+Shift+L cycle through them,
  the button shows the current mode (half circle = System, sun, moon), and the command palette
  sets each one directly. System follows Windows live; an earlier Light/Dark choice is reset to
  System once.
- Fewer false JE0013 reports: a bracketed placeholder such as `"{Name}"` is no longer called a
  document.

## 0.1.0 — 2026-09-09

First release.

- Opens files of any size instantly, browsed as a tree without loading them.
- Inspect: syntax errors explained in plain words, and the problems a validator will not find
  (JE0002–JE0013), folded by rule; a structure profile with counts and byte shares per path.
- Search keys and values as text, regular expression or whole word, with hits marked up
  everywhere the text is shown.
- Compare two documents by structure, side by side, with the changed part of a value marked;
  `--compare` on the command line lands on the comparison directly.
- Edit values, keys and members, sort properties, undo, and save without the file being
  rewritten in memory.
- Pin a key to see its value on every parent row; light and dark themes; drag and drop.
- Trimmed self-contained `win-x64` download of 20 MB.
