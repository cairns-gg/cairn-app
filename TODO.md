# TODO

Things worth doing, with enough of the reasoning to still make sense in a month. Not a
backlog to be burned down in order — the top of a list is not a priority claim.

## Taking over an imported pack

An imported pack follows its author, and that now closes off nearly everything: it cannot
be published, and it cannot be exported. Both are right on their own — publishing would
re-issue somebody else's curation under your name, and a `.cairn` file carries no author,
so handing one over launders the pack into an unowned copy.

What is missing is the way out. Someone who imports a pack, then swaps half its mods and
adds three of their own, is holding something that is theirs in every sense the word means
— and there is no action that says so. They are stuck with a pack they can neither share
nor hand to a friend.

The state machine already has the slot. `PackLink.Following` is documented as "cleared by
Take over, which keeps `Url` so the pack can still say what it diverged from", and
`ShareStatus.Following` says "the button is not offered at all — Take over comes first".
Nothing implements it.

Wanted:

- **A Take over action** on a followed pack. It clears `Following`, keeps `Url` as the
  record of what this came from, and switches `Role` to Author. Publishing then mints a
  new pack at the taker's own URL — never a revision of the original.
- **A confirmation that is honest about the trade**: after taking over, the author's
  updates stop arriving. That is the actual cost and the only reason to hesitate.
- Probably **surface the ancestry** afterwards — "forked from cairns.gg/dizzyd/anego" —
  both as courtesy to the original author and because it is genuinely useful to know.

Worth deciding before building: whether taking over is reversible (going back to following
means discarding local changes, so it is really "re-import"), and whether it should offer
to rename the pack, since keeping the original's name and id is how two different packs end
up looking like the same one.

An earlier note from the same conversation, still unbuilt: an imported pack should also
resist casual mod edits by default, with the same explicit action to signal you are taking
it on rather than following it. Take over is the one gesture both of these want.
