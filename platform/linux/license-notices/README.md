# Supplemental dependency notices

These are unmodified upstream license files omitted from the corresponding published crates. `manifest.json` pins their upstream URL, commit and SHA-256. The builder verifies the notice hash and the crate's published VCS commit where available, copies the notice into the installed documentation, and includes this directory in the complete source archive. Missing notices stop the build.

The two winapi target crates do not publish VCS metadata. Their notices come from the matching version 0.4.0 target packages in upstream's pinned 0.3.9 repository release. MIT is the chosen license where these supplemental files cover a dual-licensed crate.
