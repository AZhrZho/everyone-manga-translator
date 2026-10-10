# EMT Studio v0.1.2

- Retry OpenAI Responses and Chat Completions once without `prompt_cache_retention` when the endpoint explicitly rejects that parameter. Preserve `prompt_cache_key` and the original request timeout.
- Preserve unrelated HTTP errors and honor cancellation during compatibility retries.
- Include desktop launcher tests in the Windows release checks.
- Add Windows CI for .NET tests, client tests/lint/build, and desktop launcher tests.
- Include `release-manifest.json` with the source commit, working tree status, completed build checks, and installer SHA-256.

Windows x64 installer; CRAFT and CTD models are bundled.
