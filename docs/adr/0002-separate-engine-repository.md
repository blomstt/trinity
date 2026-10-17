# 2. Trinity lives in its own repository

Date: 2026-10-17. Status: accepted.

## Context

The engine and the game could share one repo, or the engine could stand alone the way Ludeon's Waylith does for RimWorld.

## Decision

Trinity is a separate repository (`~/Projects/trinity`). The Varendal game lives in its own repository and depends on Trinity.

## Consequences

- A hard boundary: nothing Varendal-specific goes into Trinity. Game rules, data and content stay in the game repo.
- During M1 the game references Trinity's projects by path, with the two repos side by side in `~/Projects`, so changes to both can be made at once. Once the APIs settle, Trinity ships as NuGet packages instead.
- Engine changes that a game needs are made in Trinity first, then used from the game.
