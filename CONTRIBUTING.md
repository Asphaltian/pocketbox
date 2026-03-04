# Contributors Guidelines

## Reporting Bugs

Please follow these guidelines when reporting a bug:
* Be thorough in your report. Tell us everything — what ROM you were running, what you expected, what happened instead.
* If you can reproduce the bug, give us step-by-step instructions.
* Include screenshots or short recordings if the issue is visual (rendering glitches, incorrect colors, etc.).
* Make sure your bug hasn't already been reported.

## Feature Requests

Please follow these guidelines when requesting a feature:
* Tell us why you need this feature, what it adds, and what you've tried.
* Make sure your feature hasn't already been requested.

## Making Changes

### Fixing Bugs

If you're fixing a bug, make sure you reference any applicable bug reports, explain what the problem was and how it was solved.

Unit tests are always great where applicable.

### Adding New Features

Before you start trying to add a new feature, it should be something people want and has been discussed in a proposal issue ideally.

### Guidelines

A few guidelines that will make it easier to review and merge your changes:

* **Scope**
    * Keep your pull requests focused and avoid unnecessary changes.
* **Commits**
    * Should group relevant changes together, the message should explain concisely what it's doing, there should be a longer summary elaborating if required.
    * Remove unnecessary commits and squash commits together where appropriate.
* **Formatting**
    * Your IDE should adhere to the style set in `.editorconfig`.
* **Testing**
    * If your change affects emulation accuracy, verify it against the [mGBA test suite](https://github.com/mgba-emu/suite) and note results in the PR.
