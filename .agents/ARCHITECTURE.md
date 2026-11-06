# Project architecture and verification context

This is a project template, not a statement that an unspecified stack is installed.
Fill it with verified repository facts as project setup work, not guesses. Agents should
inspect the repository for missing information and ask only when absence blocks safe work.
Read the relevant section before changing a subsystem.

## Purpose and boundaries

Project goal, users, subsystem responsibilities, and protected/out-of-scope areas: not recorded.

## Existing documentation

The supplied bundle identifies `/input` as the location of initial AGV documentation.
That is a project-specific absolute path; verify its availability. Do not create it or
assume it exists on another host. Update the location here when the project owner supplies it.

## Toolchain and supported environments

Record language/runtime/compiler versions, package manager, lockfiles, OS constraints,
and external/hardware dependencies. No versions have been supplied.

## Build and verification commands

Record exact commands, working directories, prerequisites, expected outcomes, and side
effects for setup, build, lint/type checks, unit/integration tests, and hardware tests.
No project build or test commands have been supplied. Do not substitute example commands
from a skill. Bundle self-tests are documented separately in `tools/README.md`.

## Hardware topology and task/program layout

Record controllers, I/O ownership, cycle times, communication paths, and relevant diagrams.
Not recorded in the supplied bundle.

## Error model and approved safe states

Record failure handling, recovery, safety boundaries, and links to approved machine-specific
designs. Missing safety requirements must not be filled with generic assumptions.

## Decisions and known issues

Record why the system is structured this way; link evidence and `.agents/ISSUES.md`.

## Future direction

Owner-approved plans belong in `.agents/ROADMAP.md`; do not treat ideas as authorized scope.
