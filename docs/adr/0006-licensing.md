# 6. Licensing: PolyForm Noncommercial, commercial licences on request

Date: 2026-10-17. Status: accepted.

## Context

Trinity goes public, and blomst may sell it later. It's built on BRUTAL (BRUTAL License v1.0: MIT terms, plus logo, credit and NOTICE conditions for any distributed product, with no royalties). BRUTAL explicitly allows building and selling an engine on top of it.

## Decision

- Trinity's code is under the PolyForm Noncommercial License 1.0.0. Commercial use needs a separate licence from blomst (COMMERCIAL.md).
- Contributors grant blomst the right to relicense their contributions, so dual licensing stays possible (CONTRIBUTING.md).
- BRUTAL's conditions are met by default in everything Trinity builds, Trinity Editor included: logo splash, `Credits.Required`, `NOTICE.txt`, and every package's licence and notice files copied into `licenses/` by the build.

## Consequences

- Hobby and learning use is free; companies pay. Opening up further later (MIT, for example) stays possible; narrowing an open licence wouldn't be.
- The name "Trinity" has been used by other engine projects (a retro 2.5D engine, a GoldSrc renderer, the former name of Quadrion). Check trademarks before a commercial launch, and be ready to rename.
- Not legal advice. Before selling licences, have a lawyer review the commercial licence terms.
