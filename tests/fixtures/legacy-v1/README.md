# Frozen v1 consumer

`ItemDetailsClient.cs` is an unmodified copy of Item Details commit `b340587`'s
`sdk/ItemDetailsClient.cs`, before `group` and the badge endpoint existed.

Do not update this file alongside the current SDK. `test_legacy_compat.ps1` compiles
it in a separate consumer assembly with game/framework references only, then uses
it against the current producer. A new SDK fixture must be added separately.

The same test covers the config-based compatibility promised for SectionedTooltips
1.6.2 (`68de0ca`) and 1.6.3 (`ed5379e`). Their `Enabled` / `ContentsPanel` defaults
are true and their container panel behavior is unchanged between these revisions.
It uses in-memory config entries and never starts the game or writes player config.
