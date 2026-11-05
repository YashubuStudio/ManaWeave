# Headless verification

Run `Tools/test-headless.sh` or `Tools/test-headless.ps1` with .NET 8 SDK.
The core and inference libraries are additionally compiled against .NET Standard 2.1, with C# 7.3 and warnings-as-errors. These are ordinary console harnesses and require no test-package downloads.

- `CoreHarness.cs`: world laws, resources, spells, replay and serialization.
- `MagicHarness.cs`: original models, DSL, rejection, Python/C# numerical parity and world-engine integration.
- `LearnedAcceptance.cs`: observed acceptance/accuracy of the frozen learned model and direct-weight parser. Read its arguments or the LLM README for execution.

The test executable runtime is not the Unity scripting runtime. Runtime shader/import/play checks are separately pending because Unity Editor was unavailable during authoring. Temporary Unity API stubs used for a syntax/interface sanity check are not shipped as production code or claimed to emulate Unity.
