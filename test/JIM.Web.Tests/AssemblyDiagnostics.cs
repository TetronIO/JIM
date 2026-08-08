// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using NUnit.Framework;

// Enables per-test memory snapshots when JIM_TEST_MEMORY_LOG is set. No-op otherwise.
[assembly: JIM.TestSupport.MemoryLogging]

// A fresh fixture instance, and so a fresh bUnit context, for every test in this assembly.
//
// NUnit's default is one fixture instance per fixture, and the component fixtures here ARE their bUnit context
// (they derive from JimComponentTestContext), so by default every test in a fixture shares one renderer and
// nothing an earlier test rendered is ever disposed. Components that arm timers keep firing into that shared
// renderer while later tests assert against it: SetPasswordDialogTests, whose dialog re-conceals a revealed
// password on a timer, was seen failing once under full-solution load and passing on every isolated re-run.
// Isolating it also cut that fixture from 34s to 7s, because the renderer was no longer carrying 43 tests'
// worth of live components.
//
// Declared once here rather than per fixture so a new component fixture is isolated by default rather than by
// remembering. No fixture in this assembly uses [OneTimeSetUp] or otherwise depends on sharing state between
// its tests.
[assembly: FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
