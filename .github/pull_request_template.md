## What and why

## Checks
- [ ] `dotnet format --verify-no-changes` and `dotnet build -c Release -warnaserror` are clean
- [ ] `dotnet test --filter-not-trait Category=Hardware` passes (and the hardware tests, if the change touches the radio)
- [ ] New logic in `Core` has unit tests
- [ ] Still receive-only: no transmit call added, antenna power and amp still default off
- [ ] Security-relevant? (parsing untrusted data, files, network, process launch) -> docs/threat-model.md updated
- [ ] UI change? Checked at 100/150/200% scaling, light/dark/high contrast, keyboard only and with Narrator
- [ ] CHANGELOG.md updated under [Unreleased]
