# Checkup look (Windows 11 Settings calm)

- No hairlines: Checkup ignores the theme's row style. Each group is one card (`Ui.Surface`: radius 8, `FuseboxCardFill` + 1 px `FuseboxLine`; glass fill/rim in Liquid Glass, block colour with white text in Color blocks).
- Group header = flat button (icon, title, one-line summary, chevron) at the top of its card; content opens inside the same card. No Expander chrome, no animation; `checkupOpen`, lazy build and accessible names stay.
- Inside a group: plain sections (13 semibold title, secondary text), 20 px apart, two top-aligned columns from 640 px inner width; icons only for real problems.
- Buttons: one accent `Primary` per group ("Check now"), every other action a text link (`Ui.Link`, danger colour for Stop). No outlined capsules; verdict tags are soft tinted badges.
- Tips: soft tinted blocks (icon, bold head, text, one link); a tip doesn't repeat a link its card already has.
- Score card: one calm card, big score + "/10" only with a score. Page padding 16, content max 1000 px centred.
- Startup rows: name + usage, state, switch on one centred line; disk rows: the whole row opens Explorer (name + badge, bar + size, advice). Today's free space uses the report's value everywhere.
