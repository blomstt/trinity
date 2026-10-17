# Contributing

Thanks for wanting to help. One condition first, because Trinity is offered both non-commercially and under paid commercial licences (see [COMMERCIAL.md](COMMERCIAL.md)):

**By submitting a contribution (code, shaders, assets, documentation), you confirm that you wrote it or have the right to submit it, and you grant blomst a perpetual, worldwide, non-exclusive, royalty-free, irrevocable licence to use, modify and distribute it under any licence terms, including commercial ones.** You keep the copyright to your work.

If you can't agree to that, please open an issue describing the change instead of a pull request.

Practicalities:

- Keep it light. Trinity follows BRUTAL's approach: nothing ships or runs that games don't need. Say in the PR what a new dependency is for, and how it affects build size, memory or startup.
- `dotnet build` and `dotnet test tests/Trinity.Tests` should pass. Shaders need `glslc` (from shaderc).
- Don't paste in third-party code or assets unless their licence allows commercial use and redistribution. Credit them in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) or the sample's `CREDITS.md`.
