// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Assembly visibility for tests

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("com.npctts.unity.editmode.tests")]
[assembly: InternalsVisibleTo("com.npctts.unity.playmode.tests")]
[assembly: InternalsVisibleTo("com.npctts.unity.editor")]
