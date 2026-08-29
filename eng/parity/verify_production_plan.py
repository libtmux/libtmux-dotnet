"""Validate the ignored C# production implementation plan."""

# ruff: noqa: E501

from __future__ import annotations

import argparse
import collections
import copy
import functools
import json
import pathlib
import re
import runpy
import shlex
import subprocess
import sys
import typing as t

LEDGER_PATH = (
    pathlib.Path(__file__).parents[2] / "docs" / "parity" / "parity-ledger.json"
)
CSHARP_ROOT = pathlib.Path(__file__).parents[2]
PUBLIC_API_PATH = CSHARP_ROOT / "docs" / "public-api.json"
INVENTORY_PATH = CSHARP_ROOT / "docs" / "parity" / "python-public-api.json"
ERROR_POLICIES_PATH = CSHARP_ROOT / "docs" / "parity" / "error-policies.json"
PUBLIC_API_VALIDATOR_PATH = pathlib.Path(__file__).with_name("verify_public_api.py")
LEDGER_VALIDATOR_PATH = pathlib.Path(__file__).with_name("verify_ledger.py")
COMPONENT_IDS = frozenset(range(1, 19))
COMPONENT_FILES: dict[int, tuple[str, ...]] = {
    1: (
        "LibTmux.slnx",
        "src/LibTmux/LibTmux.csproj",
        "src/LibTmux/packages.lock.json",
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        "tests/LibTmux.UnitTests/packages.lock.json",
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "tests/LibTmux.IntegrationTests/packages.lock.json",
        "src/LibTmux/Transport/TmuxCommandRequest.cs",
        "src/LibTmux/Transport/TmuxCommandResult.cs",
        "src/LibTmux/Transport/TmuxProcessTransport.cs",
        "src/LibTmux/Transport/TmuxCommandDispatcher.cs",
        "src/LibTmux/Transport/TmuxCommandFailure.cs",
        "src/LibTmux/Transport/TmuxTransportLimits.cs",
        "src/LibTmux/Transport/Utf8BackslashDecoder.cs",
        "src/LibTmux/Server.cs",
        "src/LibTmux/Session.cs",
        "src/LibTmux/Window.cs",
        "src/LibTmux/Pane.cs",
        "src/LibTmux/Client.cs",
        "src/LibTmux/Server.Command.cs",
        "src/LibTmux/Session.Command.cs",
        "src/LibTmux/Window.Command.cs",
        "src/LibTmux/Pane.Command.cs",
        "tests/LibTmux.UnitTests/Entities/EntityShellTests.cs",
        "tests/LibTmux.UnitTests/Transport/TmuxProcessTransportTests.cs",
        "tests/LibTmux.IntegrationTests/Transport/ProcessTransportTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component01ParityTests.cs",
        "src/LibTmux/Exceptions/LibTmuxException.cs",
        "src/LibTmux/Exceptions/TmuxCommandException.cs",
        "src/LibTmux/Exceptions/TmuxCommandNotFoundException.cs",
        "src/LibTmux/Exceptions/TmuxTransportException.cs",
        "src/LibTmux/Exceptions/TmuxOperationCanceledException.cs",
        "src/LibTmux/Exceptions/TmuxCleanupException.cs",
        "src/LibTmux/Exceptions/TmuxWaitTimeoutException.cs",
        "tests/LibTmux.IntegrationTests/Infrastructure/RawTmuxTestContext.cs",
        "tests/LibTmux.IntegrationTests/Infrastructure/ControlModeClientScope.cs",
        "tests/LibTmux.IntegrationTests/Infrastructure/PtyAttachedClientScope.cs",
        "eng/parity/require_red.py",
        "eng/parity/tests/test_require_red.py",
        "tests/LibTmux.TestChild/LibTmux.TestChild.csproj",
        "tests/LibTmux.TestChild/packages.lock.json",
        "tests/LibTmux.TestChild/Program.cs",
    ),
    2: (
        "src/LibTmux/Connection/TmuxConnection.cs",
        "src/LibTmux/Connection/TmuxConnectionOptions.cs",
        "src/LibTmux/Connection/ServerGeneration.cs",
        "src/LibTmux/Server.Identity.cs",
        "src/LibTmux/Session.Identity.cs",
        "src/LibTmux/Window.Identity.cs",
        "src/LibTmux/Pane.Identity.cs",
        "src/LibTmux/Targets/TmuxTarget.cs",
        "src/LibTmux/SessionId.cs",
        "src/LibTmux/WindowId.cs",
        "src/LibTmux/PaneId.cs",
        "src/LibTmux/TmuxColorMode.cs",
        "tests/LibTmux.UnitTests/Connection/TmuxConnectionTests.cs",
        "tests/LibTmux.IntegrationTests/Connection/ServerGenerationTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component02ParityTests.cs",
        "src/LibTmux/Exceptions/StaleServerGenerationException.cs",
        "src/LibTmux/Exceptions/TmuxObjectNotFoundException.cs",
    ),
    3: (
        "src/LibTmux/Constants/TmuxConstants.cs",
        "src/LibTmux/Constants/TmuxEnums.cs",
        "src/LibTmux/Formats/TmuxFormats.cs",
        "src/LibTmux/Versioning/TmuxVersion.cs",
        "src/LibTmux/Server.Version.cs",
        "src/LibTmux/Versioning/TmuxCapabilities.cs",
        "src/LibTmux/Internal/CommandFlagCatalog.cs",
        "src/LibTmux/Internal/FormatCatalog.cs",
        "src/LibTmux/Internal/FormatFieldDescriptor.cs",
        "tests/LibTmux.UnitTests/Versioning/TmuxCapabilitiesTests.cs",
        "tests/LibTmux.IntegrationTests/Versioning/VersionParityTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component03ParityTests.cs",
        "src/LibTmux/Exceptions/TmuxVersionTooLowException.cs",
        "docs/parity/version-deltas.json",
    ),
    4: (
        "src/LibTmux/Materialization/FormatProjection.cs",
        "src/LibTmux/Materialization/SeparatedRowFramer.cs",
        "src/LibTmux/Materialization/TmuxMaterializer.cs",
        "src/LibTmux/Materialization/TmuxMaterializationQuery.cs",
        "src/LibTmux/Materialization/MaterializationContext.cs",
        "src/LibTmux/Materialization/EntityMaterializationState.cs",
        "tests/LibTmux.UnitTests/Materialization/SeparatedRowFramerTests.cs",
        "tests/LibTmux.UnitTests/Materialization/FormatProjectionTests.cs",
        "tests/LibTmux.UnitTests/Materialization/TmuxMaterializerTests.cs",
        "tests/LibTmux.IntegrationTests/Materialization/MaterializationTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs",
    ),
    5: (
        "src/LibTmux/Snapshots/CapturedRelation.cs",
        "src/LibTmux/Snapshots/SnapshotDepth.cs",
        "src/LibTmux/Snapshots/ServerSnapshot.cs",
        "src/LibTmux/Snapshots/WindowEntityKey.cs",
        "src/LibTmux/Snapshots/SessionWindowEdge.cs",
        "src/LibTmux/Session.Relations.cs",
        "src/LibTmux/Window.Relations.cs",
        "src/LibTmux/Pane.Relations.cs",
        "tests/LibTmux.UnitTests/Snapshots/CapturedRelationTests.cs",
        "tests/LibTmux.IntegrationTests/Snapshots/HierarchySnapshotTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component05ParityTests.cs",
        "src/LibTmux/Exceptions/IncompleteSnapshotException.cs",
    ),
    6: (
        "src/LibTmux/Environment/TmuxEnvironment.cs",
        "src/LibTmux/Environment/ChildProcessEnvironment.cs",
        "src/LibTmux/Server.Environment.cs",
        "src/LibTmux/Session.Environment.cs",
        "src/LibTmux/Window.Environment.cs",
        "src/LibTmux/Pane.Environment.cs",
        "tests/LibTmux.UnitTests/Environment/TmuxEnvironmentTests.cs",
        "tests/LibTmux.IntegrationTests/Environment/ChildEnvironmentTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component06ParityTests.cs",
    ),
    7: (
        "src/LibTmux/Collections/SnapshotCollectionExtensions.cs",
        "src/LibTmux/Collections/SnapshotLookup.cs",
        "src/LibTmux/Server.Collections.cs",
        "tests/LibTmux.UnitTests/Collections/SnapshotCollectionTests.cs",
        "tests/LibTmux.IntegrationTests/Collections/ScopedCollectionTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component07ParityTests.cs",
    ),
    8: (
        "src/LibTmux/Query/QueryDocument.cs",
        "src/LibTmux/Query/QueryNode.cs",
        "src/LibTmux/Query/QueryTranslator.cs",
        "src/LibTmux/Query/QueryInterpreter.cs",
        "src/LibTmux/Query/NativeFilterSearch.cs",
        "src/LibTmux/Query/QueryExtensions.cs",
        "src/LibTmux/Query/NameContainsLookupParser.cs",
        "src/LibTmux.Generators/LibTmux.Generators.csproj",
        "src/LibTmux.Generators/FieldCatalogGenerator.cs",
        "tests/LibTmux.UnitTests/Query/QuerySemanticsTests.cs",
        "tests/LibTmux.IntegrationTests/Query/NativeFilterSearchTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component08ParityTests.cs",
        "src/LibTmux/Exceptions/UnsupportedQueryExpressionException.cs",
        "src/LibTmux.Generators/packages.lock.json",
    ),
    9: (
        "src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
        "src/LibTmux.Query.Json/QueryJsonSerializerContext.cs",
        "src/LibTmux.Query.Json/QueryDocumentJsonConverter.cs",
        "src/LibTmux.Query.Json/libtmux-query-v1.schema.json",
        "tests/LibTmux.UnitTests/Query/QueryJsonTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component09ParityTests.cs",
        "src/LibTmux.Query.Json/packages.lock.json",
    ),
    10: (
        "src/LibTmux/Server.Lifecycle.cs",
        "src/LibTmux/Session.Lifecycle.cs",
        "src/LibTmux/Requests/NewSessionRequest.cs",
        "src/LibTmux/Requests/AttachSessionRequest.cs",
        "src/LibTmux/Testing/TemporaryServerScope.cs",
        "src/LibTmux/Testing/TemporarySessionScope.cs",
        "tests/LibTmux.IntegrationTests/Hierarchy/ServerSessionLifecycleTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component10ParityTests.cs",
        "src/LibTmux/Exceptions/TmuxSessionExistsException.cs",
        "src/LibTmux/Internal/SessionName.cs",
        "src/LibTmux/Internal/StartDirectory.cs",
    ),
    11: (
        "src/LibTmux/Session.WindowNavigation.cs",
        "src/LibTmux/Window.Topology.cs",
        "src/LibTmux/Requests/NewWindowRequest.cs",
        "src/LibTmux/Requests/MoveWindowRequest.cs",
        "src/LibTmux/Requests/LinkWindowRequest.cs",
        "src/LibTmux/Requests/ResizeWindowRequest.cs",
        "src/LibTmux/Requests/SelectLayoutRequest.cs",
        "src/LibTmux/Requests/SplitPaneRequest.cs",
        "src/LibTmux/Requests/DisplayMessageRequest.cs",
        "src/LibTmux/Requests/NewPaneRequest.cs",
        "src/LibTmux/Requests/RespawnRequest.cs",
        "src/LibTmux/Testing/TemporaryWindowScope.cs",
        "tests/LibTmux.IntegrationTests/Hierarchy/WindowTopologyTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component11ParityTests.cs",
        "src/LibTmux/Exceptions/TmuxWindowException.cs",
    ),
    12: (
        "src/LibTmux/Window.PaneNavigation.cs",
        "src/LibTmux/Pane.Operations.cs",
        "src/LibTmux/Requests/CapturePaneRequest.cs",
        "src/LibTmux/Requests/DisplayPopupRequest.cs",
        "src/LibTmux/Requests/SendKeysRequest.cs",
        "src/LibTmux/Requests/ResizePaneRequest.cs",
        "src/LibTmux/Requests/MovePaneRequest.cs",
        "src/LibTmux/Requests/SwapPaneRequest.cs",
        "src/LibTmux/Requests/SelectPaneRequest.cs",
        "src/LibTmux/Requests/CopyModeRequest.cs",
        "src/LibTmux/Requests/PasteBufferRequest.cs",
        "src/LibTmux/Requests/PipePaneRequest.cs",
        "src/LibTmux/Requests/ChooseTreeRequest.cs",
        "src/LibTmux/Requests/FindWindowRequest.cs",
        "tests/LibTmux.IntegrationTests/Hierarchy/PaneOperationsTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component12ParityTests.cs",
        "src/LibTmux/Exceptions/TmuxPaneException.cs",
    ),
    13: (
        "src/LibTmux/Server.Clients.cs",
        "src/LibTmux/Client.Administration.cs",
        "src/LibTmux/ClientAttachment.cs",
        "tests/LibTmux.IntegrationTests/Clients/ClientAdministrationTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component13ParityTests.cs",
    ),
    14: (
        "src/LibTmux/Options/TmuxOptionValue.cs",
        "src/LibTmux/Options/TmuxOptions.cs",
        "src/LibTmux/Internal/OptionParser.cs",
        "src/LibTmux/Internal/OptionFailure.cs",
        "src/LibTmux/Requests/GetOptionRequest.cs",
        "src/LibTmux/Requests/GetOptionsRequest.cs",
        "src/LibTmux/Requests/SetOptionRequest.cs",
        "src/LibTmux/Requests/UnsetOptionRequest.cs",
        "src/LibTmux/Server.Options.cs",
        "src/LibTmux/Session.Options.cs",
        "src/LibTmux/Window.Options.cs",
        "src/LibTmux/Pane.Options.cs",
        "tests/LibTmux.UnitTests/Options/TmuxOptionValueTests.cs",
        "tests/LibTmux.IntegrationTests/Options/TmuxOptionsTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component14ParityTests.cs",
        "src/LibTmux/Exceptions/TmuxOptionException.cs",
    ),
    15: (
        "src/LibTmux/Hooks/TmuxHooks.cs",
        "src/LibTmux/Environment/TmuxEnvironmentOperations.cs",
        "src/LibTmux/Requests/HookRequest.cs",
        "src/LibTmux/Requests/ListHooksRequest.cs",
        "src/LibTmux/Requests/SetHookRequest.cs",
        "src/LibTmux/Requests/SetHooksRequest.cs",
        "src/LibTmux/Server.Hooks.cs",
        "src/LibTmux/Session.Hooks.cs",
        "src/LibTmux/Window.Hooks.cs",
        "src/LibTmux/Pane.Hooks.cs",
        "src/LibTmux/Server.EnvironmentOperations.cs",
        "src/LibTmux/Session.EnvironmentOperations.cs",
        "tests/LibTmux.IntegrationTests/Hooks/HookOperationsTests.cs",
        "tests/LibTmux.IntegrationTests/Environment/EnvironmentOperationsTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component15ParityTests.cs",
    ),
    16: (
        "src/LibTmux/Utilities/ServerUtilities.cs",
        "src/LibTmux/Server.Utilities.cs",
        "src/LibTmux/Utilities/TmuxBuffer.cs",
        "src/LibTmux/Utilities/TmuxMenuItem.cs",
        "src/LibTmux/Requests/BindKeyRequest.cs",
        "src/LibTmux/Requests/CommandPromptRequest.cs",
        "src/LibTmux/Requests/ConfirmBeforeRequest.cs",
        "src/LibTmux/Requests/DisplayMenuRequest.cs",
        "src/LibTmux/Requests/IfShellRequest.cs",
        "src/LibTmux/Requests/ListBuffersRequest.cs",
        "src/LibTmux/Requests/RunShellRequest.cs",
        "src/LibTmux/Requests/ServerAccessRequest.cs",
        "src/LibTmux/Requests/UnbindKeyRequest.cs",
        "src/LibTmux/Requests/WaitForRequest.cs",
        "tests/LibTmux.IntegrationTests/Utilities/ServerUtilitiesTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component16ParityTests.cs",
    ),
    17: (
        "src/LibTmux/Diagnostics/TmuxLog.cs",
        "src/LibTmux/Compatibility/SupportedAliases.cs",
        "tests/LibTmux.UnitTests/Diagnostics/ExceptionContractTests.cs",
        "tests/LibTmux.IntegrationTests/Diagnostics/StructuredLoggingTests.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component17ParityTests.cs",
    ),
    18: (
        "src/LibTmux/Testing/TmuxWait.cs",
        "src/LibTmux/Testing/TmuxNameGenerator.cs",
        "src/LibTmux/Testing/TestEnvironment.cs",
        "src/LibTmux/Testing/TmuxTestOptions.cs",
        "src/LibTmux/Testing/TmuxTestFactory.cs",
        "src/LibTmux/Testing/TmuxTestContext.cs",
        "src/LibTmux/Testing/TemporaryHierarchyScope.cs",
        "tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj",
        "examples/LibTmux.Examples/LibTmux.Examples.csproj",
        "examples/LibTmux.Examples/Program.cs",
        "tests/LibTmux.IntegrationTests/Parity/Component18ParityTests.cs",
        "tests/LibTmux.IntegrationTests/Testing/TestingHelpersTests.cs",
        ".github/workflows/dotnet.yml",
        ".github/workflows/dotnet-tmux.yml",
        "README.md",
        "src/LibTmux/PublicAPI.Shipped.txt",
        "src/LibTmux/PublicAPI.Unshipped.txt",
        "src/LibTmux.Query.Json/PublicAPI.Shipped.txt",
        "src/LibTmux.Query.Json/PublicAPI.Unshipped.txt",
        "eng/parity/verify_workflows.py",
        "eng/parity/tests/test_workflows.py",
        "eng/parity/inspect_packages.py",
        "eng/parity/tests/test_packages.py",
        "eng/evidence/verify_source_binding.py",
        "eng/evidence/tests/test_source_binding.py",
        "tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj",
        "tests/LibTmux.AotSmoke/packages.lock.json",
        "tests/LibTmux.AotSmoke/Program.cs",
        "tests/LibTmux.PackageConsumer/packages.lock.json",
        "tests/LibTmux.PackageConsumer/Program.cs",
        "tests/LibTmux.PackageConsumer/NuGet.config",
        "tests/LibTmux.IntegrationTests/Packaging/PackageClosureTests.cs",
        "tests/LibTmux.UnitTests/Packaging/PublicApiContractTests.cs",
        "tests/LibTmux.UnitTests/Packaging/WorkflowContractTests.cs",
        "examples/LibTmux.Examples/packages.lock.json",
        "src/LibTmux.Query.Json/packages.packed.lock.json",
    ),
}
COMPONENT_API_TYPES: dict[int, tuple[str, ...]] = {
    1: (
        "T:LibTmux.Client",
        "T:LibTmux.ControlModeCommandException",
        "T:LibTmux.IControlModeSession",
        "T:LibTmux.Internal.TmuxCommandDispatcher",
        "T:LibTmux.Internal.TmuxCommandFailure",
        "T:LibTmux.Internal.TmuxProcessTransport",
        "T:LibTmux.LibTmuxException",
        "T:LibTmux.Pane",
        "T:LibTmux.Server",
        "T:LibTmux.Session",
        "T:LibTmux.TmuxCleanupException",
        "T:LibTmux.TmuxCommandException",
        "T:LibTmux.TmuxCommandNotFoundException",
        "T:LibTmux.TmuxChain",
        "T:LibTmux.TmuxChaining",
        "T:LibTmux.TmuxCommand",
        "T:LibTmux.TmuxEvent",
        "T:LibTmux.TmuxEventsDroppedEvent",
        "T:LibTmux.TmuxExitEvent",
        "T:LibTmux.TmuxNotificationEvent",
        "T:LibTmux.TmuxOutputEvent",
        "T:LibTmux.TmuxCommandResult",
        "T:LibTmux.TmuxOperationCanceledException",
        "T:LibTmux.TmuxTransportException",
        "T:LibTmux.TmuxWaitTimeoutException",
        "T:LibTmux.Window",
    ),
    2: (
        "T:LibTmux.PaneId",
        "T:LibTmux.PsmuxCaptureOptions",
        "T:LibTmux.PsmuxConnectionOptions",
        "T:LibTmux.PsmuxPane",
        "T:LibTmux.PsmuxServer",
        "T:LibTmux.PsmuxSession",
        "T:LibTmux.PsmuxWindow",
        "T:LibTmux.ServerConnectionOptions",
        "T:LibTmux.ServerGeneration",
        "T:LibTmux.SessionId",
        "T:LibTmux.StaleServerGenerationException",
        "T:LibTmux.TmuxColorMode",
        "T:LibTmux.TmuxObjectNotFoundException",
        "T:LibTmux.WindowId",
    ),
    3: (
        "T:LibTmux.Internal.CommandFlagCatalog",
        "T:LibTmux.Internal.FormatCatalog",
        "T:LibTmux.Internal.FormatFieldDescriptor",
        "T:LibTmux.LibTmuxInfo",
        "T:LibTmux.OptionScope",
        "T:LibTmux.PaneDirection",
        "T:LibTmux.ResizeDirection",
        "T:LibTmux.TmuxDispatchState",
        "T:LibTmux.TmuxVersion",
        "T:LibTmux.TmuxVersionTooLowException",
        "T:LibTmux.WindowDirection",
    ),
    4: (
        "T:LibTmux.Internal.FormatProjection",
        "T:LibTmux.Internal.SeparatedRowFramer",
        "T:LibTmux.Internal.MaterializationContext",
        "T:LibTmux.Internal.MaterializationQuery",
        "T:LibTmux.Internal.Materializer",
        "T:LibTmux.Internal.ServerProjection",
        "T:LibTmux.Internal.ServerProjectionDescriptor",
    ),
    5: (
        "T:LibTmux.CapturedRelation`1",
        "T:LibTmux.IncompleteSnapshotException",
        "T:LibTmux.SessionWindowEdge",
        "T:LibTmux.SnapshotDepth",
        "T:LibTmux.WindowEntityKey",
    ),
    6: ("T:LibTmux.TmuxEnvironment", "T:LibTmux.TmuxEnvironmentEntry"),
    7: ("not applicable",),
    8: (
        "T:LibTmux.Query.AndNode",
        "T:LibTmux.Query.BooleanConstant",
        "T:LibTmux.Query.ComparisonNode",
        "T:LibTmux.Query.ConstantNode",
        "T:LibTmux.Query.EnumConstant",
        "T:LibTmux.Query.FieldNode",
        "T:LibTmux.Query.InstantConstant",
        "T:LibTmux.Query.Int64Constant",
        "T:LibTmux.Query.NotNode",
        "T:LibTmux.Query.NullConstant",
        "T:LibTmux.Query.OrNode",
        "T:LibTmux.Query.QuantifierNode",
        "T:LibTmux.Query.QueryComparison",
        "T:LibTmux.Query.QueryConstant",
        "T:LibTmux.Query.QueryDocument",
        "T:LibTmux.Query.QueryEdgeParser",
        "T:LibTmux.Query.QueryExtensions",
        "T:LibTmux.Query.QueryNode",
        "T:LibTmux.Query.QueryQuantifier",
        "T:LibTmux.Query.QueryStringOperation",
        "T:LibTmux.Query.QueryTarget",
        "T:LibTmux.Query.RegexNode",
        "T:LibTmux.Query.StringConstant",
        "T:LibTmux.Query.StringNode",
        "T:LibTmux.Query.TypedIdConstant",
        "T:LibTmux.UnsafeTmuxFilter",
        "T:LibTmux.UnsupportedQueryExpressionException",
    ),
    9: ("T:LibTmux.Query.Json.QueryJson", "T:LibTmux.Query.Json.QueryJsonLimits"),
    10: (
        "T:LibTmux.AttachSessionRequest",
        "T:LibTmux.Internal.SessionName",
        "T:LibTmux.NewSessionRequest",
        "T:LibTmux.OwnedServerScope",
        "T:LibTmux.OwnedSessionScope",
        "T:LibTmux.Testing.TemporaryServerScope",
        "T:LibTmux.Testing.TemporarySessionScope",
        "T:LibTmux.TmuxSessionExistsException",
    ),
    11: (
        "T:LibTmux.DisplayMessageRequest",
        "T:LibTmux.LinkWindowRequest",
        "T:LibTmux.MoveWindowRequest",
        "T:LibTmux.NewPaneRequest",
        "T:LibTmux.NewWindowRequest",
        "T:LibTmux.OwnedWindowScope",
        "T:LibTmux.ResizeWindowRequest",
        "T:LibTmux.RespawnRequest",
        "T:LibTmux.SelectLayoutMode",
        "T:LibTmux.SelectLayoutRequest",
        "T:LibTmux.SplitPaneRequest",
        "T:LibTmux.Testing.TemporaryWindowScope",
        "T:LibTmux.TmuxWindowException",
        "T:LibTmux.WindowResizeMode",
        "T:LibTmux.WindowRotationDirection",
    ),
    12: (
        "T:LibTmux.CapturePanePosition",
        "T:LibTmux.CapturePaneRequest",
        "T:LibTmux.ChooseTreeRequest",
        "T:LibTmux.ChooseTreeSort",
        "T:LibTmux.CopyModeRequest",
        "T:LibTmux.DisplayPopupRequest",
        "T:LibTmux.FindWindowRequest",
        "T:LibTmux.MovePaneRequest",
        "T:LibTmux.PaneInputMode",
        "T:LibTmux.PaneSelectDirection",
        "T:LibTmux.PaneSwapDirection",
        "T:LibTmux.PasteBufferRequest",
        "T:LibTmux.PipePaneRequest",
        "T:LibTmux.PopupCloseMode",
        "T:LibTmux.ResizePaneRequest",
        "T:LibTmux.SelectPaneRequest",
        "T:LibTmux.SendKeysRequest",
        "T:LibTmux.SwapPaneRequest",
        "T:LibTmux.TmuxPaneException",
    ),
    13: ("T:LibTmux.ClientAttachment",),
    14: (
        "T:LibTmux.GetOptionRequest",
        "T:LibTmux.GetOptionsRequest",
        "T:LibTmux.Internal.OptionFailure",
        "T:LibTmux.Internal.OptionParser",
        "T:LibTmux.SetOptionRequest",
        "T:LibTmux.TmuxOption",
        "T:LibTmux.TmuxOptionException",
        "T:LibTmux.TmuxOptionState",
        "T:LibTmux.TmuxOptionValue",
        "T:LibTmux.TmuxOptions",
        "T:LibTmux.UnsetOptionRequest",
    ),
    15: (
        "T:LibTmux.HookRequest",
        "T:LibTmux.ListHooksRequest",
        "T:LibTmux.SetHookRequest",
        "T:LibTmux.SetHooksRequest",
        "T:LibTmux.TmuxHook",
        "T:LibTmux.TmuxHookEntry",
        "T:LibTmux.TmuxHooks",
    ),
    16: (
        "T:LibTmux.BindKeyRequest",
        "T:LibTmux.CommandPromptRequest",
        "T:LibTmux.ConfirmBeforeRequest",
        "T:LibTmux.DisplayMenuRequest",
        "T:LibTmux.IfShellRequest",
        "T:LibTmux.ListBuffersRequest",
        "T:LibTmux.PromptType",
        "T:LibTmux.RunShellRequest",
        "T:LibTmux.ServerAccessRequest",
        "T:LibTmux.ShowMessagesMode",
        "T:LibTmux.TmuxBuffer",
        "T:LibTmux.TmuxMenuItem",
        "T:LibTmux.TmuxWaitMode",
        "T:LibTmux.UnbindKeyRequest",
        "T:LibTmux.WaitForRequest",
    ),
    17: ("T:LibTmux.Internal.TmuxCommandContext",),
    18: (
        "T:LibTmux.Testing.TemporaryHierarchyScope",
        "T:LibTmux.Testing.TestEnvironment",
        "T:LibTmux.Testing.TmuxNameGenerator",
        "T:LibTmux.Testing.TmuxTestContext",
        "T:LibTmux.Testing.TmuxTestFactory",
        "T:LibTmux.Testing.TmuxTestOptions",
        "T:LibTmux.Testing.TmuxWait",
    ),
}
REQUIRED_FIELDS = (
    "Files",
    "API owners",
    "Shared files",
    "Depends on",
    "Project wiring",
    "Ledger rows",
    "Red behavioral test",
    "RED command",
    "RED evidence",
    "Frameworks",
    "tmux lanes",
    "Ledger updates",
    "Atomic commit",
    "Full gate",
)
TARGET_FRAMEWORKS = {"net8.0", "net10.0"}
TMUX_LANES = {"3.2a", "3.3a", "3.4", "3.5", "3.6", "3.7a", "3.7b"}
CLOSURE_GATES: dict[str, tuple[str, ...]] = {
    "Package": (
        "package metadata",
        ".nupkg",
        ".snupkg",
        "sourcelink json",
        "repository revision",
        "exact dependencies",
        "privacy redaction",
    ),
    "Public API": (
        "public api analyzer",
        "parity",
        "implementation",
        "evidence",
        "gap",
    ),
    "Independent review": (
        "framework design guidelines",
        "python-parity",
        "tmux",
        "resolv",
    ),
    "Repository quality": ("ruff", "mypy", "pytest doctests", "docs build"),
    "Diff integrity": ("run", "git diff --check"),
    "Staged scope": ("staged paths", "allow-list", "exactly"),
    "Clean worktree": ("require empty", "git status --porcelain"),
    "Publication boundary": (
        "local commits",
        "never runs push",
        "tag-creation",
        "provenance",
        "without claiming remote publication proof",
    ),
    "Platform workflow configuration": (
        "linux",
        "macos",
        "windows",
        "workflow configuration only",
        "does not execute",
        "runtime jobs",
    ),
    "macOS tmux workflow configuration": (
        "current-stable",
        "macos",
        "tmux integration",
        "workflow configuration only",
        "does not execute",
    ),
    "External workflow evidence": (
        "user-owned push",
        "run ids",
        "urls",
        "not runtime evidence",
    ),
    "Packed consumers": ("packed consumer", "net8.0", "net10.0"),
    "Executable examples": ("execute", "real-tmux", "example"),
    "NativeAOT": ("publish", "execute", "trimmed nativeaot", "net8.0", "net10.0"),
    "Final matrix evidence": (
        "source-bound",
        "final tmux matrix",
        "evidence bundle",
        "clean production commit",
        "evidence-only closure commit",
        "head^",
        "evaluated-commit tree fingerprint",
        "descendant diff",
        "final evidence root",
    ),
}
COMPONENT_RE = re.compile(r"^## Component ([0-9]+):\s+\S.*$")
FIELD_RE = re.compile(r"^### (.+?)\s*$")
LIST_TOKEN_RE = re.compile(r"^- (?P<fence>`{1,2})(?P<token>.+?)(?P=fence)\s*$")
EXACT_PATH_RE = re.compile(
    # A plan names a file by repository-relative path.
    r"^(?:(?:benchmarks|docs|eng|examples|src|tests|\.github)/"
    r"(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+"
    r"|[A-Za-z0-9_.-]+\.(?:slnx|json|props|md|sh))$"
)
BUILD_BOOTSTRAP_FILES = frozenset(
    {
        "LibTmux.slnx",
        "src/LibTmux/LibTmux.csproj",
        "src/LibTmux/packages.lock.json",
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        "tests/LibTmux.UnitTests/packages.lock.json",
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "tests/LibTmux.IntegrationTests/packages.lock.json",
    }
)
COMPONENT_DEPENDENCIES: dict[int, tuple[str, ...]] = {
    1: ("none",),
    2: ("component 1",),
    3: ("component 1", "component 2"),
    4: ("component 1", "component 2", "component 3"),
    5: ("component 2", "component 4"),
    6: ("component 1", "component 2"),
    7: ("component 1", "component 2", "component 4", "component 5"),
    8: ("component 3", "component 4", "component 5", "component 7"),
    9: ("component 8",),
    10: (
        "component 1",
        "component 2",
        "component 3",
        "component 4",
        "component 5",
        "component 7",
    ),
    11: ("component 3", "component 10"),
    12: ("component 3", "component 10", "component 11"),
    13: ("component 10", "component 11", "component 12"),
    14: ("component 1", "component 2", "component 3"),
    15: ("component 1", "component 2", "component 3", "component 14"),
    16: ("component 10", "component 12", "component 13"),
    17: tuple(f"component {component}" for component in range(1, 17)),
    18: tuple(f"component {component}" for component in range(1, 18)),
}
COMPONENT_SHARED_FILES: dict[int, tuple[str, ...]] = dict.fromkeys(
    COMPONENT_IDS,
    ("docs/parity/parity-ledger.json",),
)
COMPONENT_SHARED_FILES[3] += (
    "eng/tmux/build-version.sh",
    "eng/tmux/run-matrix.sh",
    "eng/evidence/assemble_bundle.py",
    "eng/evidence/tests/test_transactions.py",
    "eng/parity/reconcile_versions.py",
    "eng/parity/tests/test_reconcile_versions.py",
    "eng/evidence/validate.py",
    "eng/evidence/tests/test_validate.py",
    "tests/LibTmux.IntegrationTests/Infrastructure/PtyAttachedClientScope.cs",
)
COMPONENT_SHARED_FILES[4] += (
    "src/LibTmux/Server.Identity.cs",
    "src/LibTmux/Session.Identity.cs",
    "src/LibTmux/Window.Identity.cs",
    "src/LibTmux/Pane.Identity.cs",
    "src/LibTmux/Transport/TmuxTransportLimits.cs",
    "src/LibTmux/Transport/Utf8BackslashDecoder.cs",
    "src/LibTmux/Internal/FormatCatalog.cs",
)
COMPONENT_SHARED_FILES[5] += (
    "src/LibTmux/Materialization/EntityMaterializationState.cs",
)
COMPONENT_SHARED_FILES[8] += (
    "LibTmux.slnx",
    "src/LibTmux/LibTmux.csproj",
)
COMPONENT_SHARED_FILES[9] += ("LibTmux.slnx",)
COMPONENT_SHARED_FILES[9] += (
    "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
    "tests/LibTmux.UnitTests/packages.lock.json",
)
VERSION_POLICY_SHARED_FILES = (
    "docs/parity/version-deltas.json",
    "tests/LibTmux.IntegrationTests/Versioning/VersionParityTests.cs",
    "eng/parity/reconcile_versions.py",
    "eng/parity/tests/test_reconcile_versions.py",
)
VERSION_POLICY_OWNER_COMPONENTS = (10, 11, 12, 13, 15, 16)
# Policy rows stay pending until cohort closure, so a policy-owning component
# never edits the version-policy documents; declaring them shared here would
# demand a change the component has no cause to make.
_VERSION_POLICY_NAMESPACE = runpy.run_path(
    str(pathlib.Path(__file__).with_name("reconcile_versions.py"))
)
VERSION_POLICY_PROOFS_BY_COMPONENT: dict[int, tuple[str, ...]] = {
    component_id: tuple(
        (
            f"{capability} | {test} | supported="
            f"{_VERSION_POLICY_NAMESPACE['POLICY_PROOF_CONTRACTS'][capability]['supportedBoundary']}"
            " | unsupported="
            f"{_VERSION_POLICY_NAMESPACE['POLICY_PROOF_CONTRACTS'][capability]['unsupportedBoundary']}"
            " | evidenceStatus=pending until cohort closure"
        )
        for capability, components in _VERSION_POLICY_NAMESPACE[
            "POLICY_OWNER_COMPONENTS"
        ].items()
        for owner, test in zip(
            components,
            _VERSION_POLICY_NAMESPACE["POLICY_WRAPPER_TESTS"][capability],
            strict=True,
        )
        if owner == component_id
    )
    for component_id in VERSION_POLICY_OWNER_COMPONENTS
}
ENTITY_SHELL_FILES = (
    "src/LibTmux/Server.cs",
    "src/LibTmux/Session.cs",
    "src/LibTmux/Window.cs",
    "src/LibTmux/Pane.cs",
    "src/LibTmux/Client.cs",
)
ENTITY_FRAGMENT_FILES: dict[int, tuple[str, ...]] = {
    1: (
        "src/LibTmux/Server.Command.cs",
        "src/LibTmux/Session.Command.cs",
        "src/LibTmux/Window.Command.cs",
        "src/LibTmux/Pane.Command.cs",
    ),
    2: (
        "src/LibTmux/Server.Identity.cs",
        "src/LibTmux/Session.Identity.cs",
        "src/LibTmux/Window.Identity.cs",
        "src/LibTmux/Pane.Identity.cs",
    ),
    3: ("src/LibTmux/Server.Version.cs",),
    5: (
        "src/LibTmux/Session.Relations.cs",
        "src/LibTmux/Window.Relations.cs",
        "src/LibTmux/Pane.Relations.cs",
    ),
    6: (
        "src/LibTmux/Server.Environment.cs",
        "src/LibTmux/Session.Environment.cs",
        "src/LibTmux/Window.Environment.cs",
        "src/LibTmux/Pane.Environment.cs",
    ),
    7: ("src/LibTmux/Server.Collections.cs",),
    10: (
        "src/LibTmux/Server.Lifecycle.cs",
        "src/LibTmux/Session.Lifecycle.cs",
    ),
    11: (
        "src/LibTmux/Session.WindowNavigation.cs",
        "src/LibTmux/Window.Topology.cs",
    ),
    12: (
        "src/LibTmux/Window.PaneNavigation.cs",
        "src/LibTmux/Pane.Operations.cs",
    ),
    13: (
        "src/LibTmux/Server.Clients.cs",
        "src/LibTmux/Client.Administration.cs",
    ),
    14: (
        "src/LibTmux/Server.Options.cs",
        "src/LibTmux/Session.Options.cs",
        "src/LibTmux/Window.Options.cs",
        "src/LibTmux/Pane.Options.cs",
    ),
    15: (
        "src/LibTmux/Server.Hooks.cs",
        "src/LibTmux/Session.Hooks.cs",
        "src/LibTmux/Window.Hooks.cs",
        "src/LibTmux/Pane.Hooks.cs",
    ),
    16: ("src/LibTmux/Server.Utilities.cs",),
}
EXCEPTION_FILES = (
    "src/LibTmux/Exceptions/LibTmuxException.cs",
    "src/LibTmux/Exceptions/TmuxCommandException.cs",
    "src/LibTmux/Exceptions/TmuxCommandNotFoundException.cs",
    "src/LibTmux/Exceptions/TmuxTransportException.cs",
    "src/LibTmux/Exceptions/TmuxOperationCanceledException.cs",
    "src/LibTmux/Exceptions/TmuxCleanupException.cs",
    "src/LibTmux/Exceptions/TmuxWaitTimeoutException.cs",
    "src/LibTmux/Exceptions/StaleServerGenerationException.cs",
    "src/LibTmux/Exceptions/TmuxObjectNotFoundException.cs",
    "src/LibTmux/Exceptions/TmuxVersionTooLowException.cs",
    "src/LibTmux/Exceptions/IncompleteSnapshotException.cs",
    "src/LibTmux/Exceptions/UnsupportedQueryExpressionException.cs",
    "src/LibTmux/Exceptions/TmuxSessionExistsException.cs",
    "src/LibTmux/Exceptions/TmuxWindowException.cs",
    "src/LibTmux/Exceptions/TmuxPaneException.cs",
    "src/LibTmux/Exceptions/TmuxOptionException.cs",
)
# Every tmux command passes through one dispatcher, so the diagnostics it
# records belong there rather than repeated in each entity.
DIAGNOSTIC_SHARED_FILES = (
    "src/LibTmux/Transport/TmuxCommandDispatcher.cs",
    "src/LibTmux/Connection/TmuxConnection.cs",
)
COMPONENT_SHARED_FILES[17] += DIAGNOSTIC_SHARED_FILES
COMPONENT_SHARED_FILES[18] += (
    "LibTmux.slnx",
    "Directory.Packages.props",
    "src/LibTmux/LibTmux.csproj",
    "src/LibTmux/packages.lock.json",
    "src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
    "src/LibTmux.Query.Json/packages.lock.json",
)
FOUNDATIONAL_FILES = frozenset(
    {
        *ENTITY_SHELL_FILES,
        *EXCEPTION_FILES[:7],
        "src/LibTmux/Transport/TmuxCommandDispatcher.cs",
        "src/LibTmux/Transport/TmuxCommandFailure.cs",
        "src/LibTmux/Transport/TmuxTransportLimits.cs",
        "tests/LibTmux.IntegrationTests/Infrastructure/RawTmuxTestContext.cs",
        "tests/LibTmux.IntegrationTests/Infrastructure/ControlModeClientScope.cs",
        "tests/LibTmux.IntegrationTests/Infrastructure/PtyAttachedClientScope.cs",
        "tests/LibTmux.TestChild/LibTmux.TestChild.csproj",
        "tests/LibTmux.TestChild/packages.lock.json",
        "tests/LibTmux.TestChild/Program.cs",
    }
)
PROJECT_WIRING: dict[int, tuple[str, ...]] = {
    1: (
        "mise exec -- dotnet sln LibTmux.slnx add src/LibTmux/LibTmux.csproj tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj tests/LibTmux.TestChild/LibTmux.TestChild.csproj",
        "mise exec -- dotnet add tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj reference src/LibTmux/LibTmux.csproj",
        "mise exec -- dotnet add tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj reference src/LibTmux/LibTmux.csproj",
        "src/LibTmux/LibTmux.csproj declares InternalsVisibleTo for LibTmux.UnitTests and LibTmux.IntegrationTests",
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj references Microsoft.CodeAnalysis.CSharp so EntityShellTests parses source syntax instead of reflecting partial declarations",
        "Server, Session, Window, and Pane shells expose an internal dispatcher plus default-target constructor seam used by their Component 1 command fragments before public Open and typed IDs arrive",
    ),
    8: (
        "mise exec -- dotnet sln LibTmux.slnx add src/LibTmux.Generators/LibTmux.Generators.csproj",
        "src/LibTmux/LibTmux.csproj references src/LibTmux.Generators/LibTmux.Generators.csproj with OutputItemType=Analyzer and ReferenceOutputAssembly=false",
    ),
    9: (
        "mise exec -- dotnet sln LibTmux.slnx add src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
        "mise exec -- dotnet add src/LibTmux.Query.Json/LibTmux.Query.Json.csproj reference src/LibTmux/LibTmux.csproj",
        "mise exec -- dotnet add tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj reference src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
    ),
    18: (
        "mise exec -- dotnet sln LibTmux.slnx add tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj examples/LibTmux.Examples/LibTmux.Examples.csproj",
        "mise exec -- dotnet add examples/LibTmux.Examples/LibTmux.Examples.csproj reference src/LibTmux/LibTmux.csproj src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
        "Directory.Packages.props declares exact central PackageVersion entries for LibTmux and LibTmux.Query.Json at [0.1.0-local]",
        "tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj and tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj declare versionless PackageReference entries for LibTmux and LibTmux.Query.Json so Central Package Management supplies [0.1.0-local]",
        "src/LibTmux.Query.Json/LibTmux.Query.Json.csproj declares its LibTmux ProjectReference only when UsePackedLibTmux is not true",
        "src/LibTmux.Query.Json/LibTmux.Query.Json.csproj declares a versionless LibTmux PackageReference only when UsePackedLibTmux is true so Central Package Management supplies exactly [0.1.0-local]",
        "src/LibTmux.Query.Json/LibTmux.Query.Json.csproj sets NuGetLockFilePath to $(MSBuildProjectDirectory)/packages.packed.lock.json only when UsePackedLibTmux is true and otherwise uses packages.lock.json",
        "default and packed Query.Json locked restores use distinct owned lock files for their mutually exclusive dependency graphs",
        "src/LibTmux/LibTmux.csproj and src/LibTmux.Query.Json/LibTmux.Query.Json.csproj declare PackageReference Include=Microsoft.CodeAnalysis.PublicApiAnalyzers with PrivateAssets=all",
    ),
}
COMPONENT_ONE_TRANSPORT_CONTRACT = (
    "ExecuteCommandAsync accepts exactly one logical tmux command; a literal ; argument remains data and is never a structural separator",
    "Only the internal TmuxCommandRequest and transport overload represent structural command groups with typed separators; no public grouping overload exists",
    "TmuxCommandResult.Arguments is a defensively copied logical argument sequence that excludes the tmux binary, connection prefixes, and guard arguments, and record equality compares the sequence deeply",
    "StandardOutput and StandardError preserve exact bytes while line projections normalize CRLF and lone CR to LF with Python universal-newline behavior",
    "ThrowIfFailed throws for any nonempty projected stderr regardless of exit code; has-session stderr leniency is dispatcher policy and never mutates raw bytes",
    "TmuxTransportLimits is an internal seam with MaxArguments=4096, MaxCapturedBytesPerStream=64 MiB, and CleanupTimeout=5s defaults; argument or stream overflow raises TmuxTransportException and every post-start failure performs bounded cleanup",
    "PtyAttachedClientScope uses script-backed PTY execution on Linux and macOS, or a behaviorally equivalent PTY implementation, and has an executable smoke test",
    "TmuxProcessTransport injects internal launcher and clock seams plus TmuxTransportLimits so tests control process start, deadlines, pumps, descendants, and cleanup faults without wall-clock sleeps",
    "LibTmux.TestChild exposes deterministic modes for arbitrary concurrent raw stdout and stderr, invalid bytes, partial final output, nonzero exit, a held pump, descendant survival, and cleanup faults",
    "Server.cs, Session.cs, Window.cs, Pane.cs, and Client.cs remain declaration-only; dispatcher fields and internal constructors for command-capable entities live only in Server.Command.cs, Session.Command.cs, Window.Command.cs, and Pane.Command.cs",
)
C4_MATERIALIZATION_CONTRACT = (
    "Each row carries exactly projection.Fields.Count values, each terminated by FormatProjection.RowSeparator; wire names are not sent and values are read positionally; every field is expanded exactly once, because a byte-count prefix would expand it twice and a field that moved in between would desynchronise the payload; copied value bytes remain undecoded until Utf8BackslashDecoder",
    "tmux LF separates rows, CRLF is accepted, and a complete final row may end at EOF; embedded CR and LF remain value data",
    "Empty values map to null with their key present; a row that ends before every field is read, a value that never closes, an oversized value, and a row not terminated by a newline each throws InvalidDataException; returned memories are copied",
    "TmuxTransportLimits adds MaxFramedFieldBytes=64 MiB by default, requires a positive value no greater than MaxCapturedBytesPerStream, and SeparatedRowFramer enforces it per value",
    "MaterializationQuery maps low-level InvalidDataException to TmuxTransportException carrying the logical tmux arguments",
    "FormatCatalog.ObjProjection contains 178 Obj fields; the existing catalog union is 82 with overlap 72, adds 106 fields, and yields 188 combined fields",
    "Format scopes contain universal=9, session=23, window=34, pane=70, client=25, buffer=3, event=9, and context=5 fields",
    "client_uid, client_user, pane_dead_signal, and pane_dead_time require tmux 3.3; the eleven approved 3.7 fields require tmux 3.7; every other field requires tmux 3.2a",
    "FormatProjection.Create emits 123/125/136 fields for sessions, windows, and panes at 3.2a/3.3a-3.6/3.7a+, and 146/150/161 fields for clients; FramedFieldCount is twice Fields.Count",
    "MaterializationQuery.FetchAsync returns all decoded dictionaries; FetchOneAsync uses the canonical tmux session for window and pane lookup, returns one dictionary, distinguishes a missing target from an unreachable server, and accepts a final CancellationToken",
    "Materializer dictionary overloads create Session, Window, and Pane handles with explicit MaterializationContext.Server ownership after Utf8BackslashDecoder projects copied raw values",
    "Private EntityMaterializationState carries copied raw fields, the owning Server, parent SessionId and WindowId identities, a Window SessionWindowEdge, and default uncaptured relation slots; an internal replacement or factory path lets Component 5 assign edge ordinals and captured relations without editing Component 4 files",
    "Every materialized row carries universal pid and start_time; MaterializationQuery rejects an unmaterialized MaterializationContext.Server.Generation before live acquisition, and both MaterializationQuery and Materializer reject parsed generation unequal to the owner with StaleServerGenerationException; MaterializationTests.Materializer_uses_server_context_and_returns_typed_raw_fields proves this owner and generation validation",
)
SOLUTION_RESTORE_PAIR = (
    "mise exec -- dotnet restore LibTmux.slnx",
    "mise exec -- dotnet restore LibTmux.slnx --locked-mode",
)
RED_BOOTSTRAP: dict[int, tuple[str, ...]] = {
    1: (
        "Create compile-ready C1 production signature stubs, TestChild modes, the selected behavioral test, require_red.py, and require_red tests before restore; do not implement the behavior and never hand-author lock files",
        *SOLUTION_RESTORE_PAIR,
        "uv run pytest eng/parity/tests/test_require_red.py",
    ),
    8: (
        "Create compile-ready generator, core, and test signature stubs plus the selected behavioral test before restore; do not implement the behavior and never hand-author lock files",
        *SOLUTION_RESTORE_PAIR,
    ),
    9: (
        "Create compile-ready Query.Json and unit-test signature stubs plus the selected behavioral test before restore; do not implement the behavior and never hand-author lock files",
        *SOLUTION_RESTORE_PAIR,
    ),
}
RED_RUNNER_CONTRACT = (
    "require_red.py invokes Microsoft Testing Platform with --no-restore, --filter-method for the exact declared --test identity, and the xUnit TRX reporter at the exact evidence path",
    "require_red.py accepts only a nonzero test-process exit with well-formed TRX containing at least one executed test and the selected behavioral test exactly once with outcome Failed",
    "require_red.py rejects build or discovery failures, zero tests, all skipped tests, aborted or canceled runs, malformed or missing TRX, unexpected test identities, and successful test runs",
    "Every component RED command invokes require_red.py directly with Release, --no-restore, one exact --test identity, and its retained TRX path; no shell negation or failure-swallowing command may decide RED",
)
RED_EVIDENCE_FRESHNESS_CONTRACT = (
    "require_red.py removes any pre-existing evidence path before invoking dotnet test and accepts only a newly created TRX from that invocation",
)
TMUX_37_TRANSITION_PROOF_CONTRACT = (
    "Build one transition tmux 3.7 binary with eng/tmux/build-version.sh and require tmux -V = tmux 3.7.",
    "The tmux 3.7 transition proof runs only for the explicit capability cohort 0001; directory names never select behavior, and that cohort rejects the advisory master lane.",
    'The cohort-bound environment records capabilityCohort="0001" and excludes only its exact evidence output root from source-state and source-fingerprint calculations.',
    'run-matrix.sh sets LIBTMUX_TRANSITION_TMUX_3_7 to the verified 3.7 binary and writes its full source commit as transitionTmuxSourceCommits["3.7"] in environment.json.',
    "VersionParityTests.BreakPane37Workaround proves net8.0 and net10.0 behavior for exact 3.7 and 3.7a, applying the workaround only to 3.7.",
    "The redacted break-pane transition transcript has exactly four records: net8.0/3.7, net8.0/3.7a, net10.0/3.7, and net10.0/3.7a; each records observed tmux version, workaround state, and behavioral outcome.",
    "reconcile_versions.py validates the raw break-pane transcript and its 3.7 transition commit against the required 3.7a matrix source commit without attaching wrapper-policy evidence to the break-pane row.",
    "Capability cohort 0001 verifies exactly the five upstream protocol observations; all 34 command-policy rows remain evidenceStatus=pending with no evidence field until their policyOwnerComponents implement wrapper-level proofs.",
    "The two hook flag rows retain introducedIn=3.2 source history, declare supportRange=baseline across the supported tmux range beginning at 3.2a, and set unsupportedBehavior=not_applicable_below_supported_floor.",
    "Component 3 may independently mark its 50 parity-ledger rows implemented and verified; version-delta policy status does not gate or inherit those ledger transitions.",
    "results.ndjson remains exactly the seven required tmux versions crossed with net8.0 and net10.0; 3.7 is not a matrix row.",
)
C1_FAILURE_CORPUS_CONTRACT = (
    "A missing configured tmux binary throws TmuxCommandNotFoundException whose TmuxBinaryPath is the configured executable path",
    "Pre-start cancellation throws OperationCanceledException carrying the caller token and starts no process",
    "Post-start cancellation throws TmuxOperationCanceledException with CommandMayHaveExecuted=true and the direct client PID",
    "Post-start cancellation reaps only the direct client while the TestChild descendant-survival mode proves the descendant remains alive",
    "Cleanup failure throws TmuxCleanupException with the original cancellation, client PID, and cleanup failure",
    "Invalid UTF-8 projection escapes each invalid byte independently as lowercase \\xNN while StandardOutput and StandardError remain byte-exact",
)
REQUIRED_PROJECT_FILES: dict[int, frozenset[str]] = {
    1: frozenset(
        {
            "tests/LibTmux.UnitTests/Entities/EntityShellTests.cs",
        }
    ),
    3: frozenset(
        {
            "src/LibTmux/Internal/CommandFlagCatalog.cs",
            "src/LibTmux/Internal/FormatCatalog.cs",
            "src/LibTmux/Internal/FormatFieldDescriptor.cs",
        }
    ),
    4: frozenset(
        {
            "src/LibTmux/Materialization/SeparatedRowFramer.cs",
            "src/LibTmux/Materialization/MaterializationContext.cs",
        }
    ),
    8: frozenset(
        {
            "src/LibTmux.Generators/LibTmux.Generators.csproj",
            "src/LibTmux.Generators/FieldCatalogGenerator.cs",
            "src/LibTmux.Generators/packages.lock.json",
        }
    ),
    9: frozenset(
        {
            "src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
            "src/LibTmux.Query.Json/QueryJsonSerializerContext.cs",
            "src/LibTmux.Query.Json/QueryDocumentJsonConverter.cs",
            "src/LibTmux.Query.Json/libtmux-query-v1.schema.json",
            "src/LibTmux.Query.Json/packages.lock.json",
        }
    ),
    10: frozenset(
        {
            "src/LibTmux/Requests/AttachSessionRequest.cs",
        }
    ),
    11: frozenset(
        {
            "src/LibTmux/Requests/DisplayMessageRequest.cs",
        }
    ),
    12: frozenset(
        {
            "src/LibTmux/Requests/DisplayPopupRequest.cs",
        }
    ),
    18: frozenset(
        {
            ".github/workflows/dotnet.yml",
            ".github/workflows/dotnet-tmux.yml",
            "README.md",
            "src/LibTmux/PublicAPI.Shipped.txt",
            "src/LibTmux/PublicAPI.Unshipped.txt",
            "src/LibTmux.Query.Json/PublicAPI.Shipped.txt",
            "src/LibTmux.Query.Json/PublicAPI.Unshipped.txt",
            "eng/parity/verify_workflows.py",
            "eng/parity/tests/test_workflows.py",
            "eng/parity/inspect_packages.py",
            "eng/parity/tests/test_packages.py",
            "eng/evidence/verify_source_binding.py",
            "eng/evidence/tests/test_source_binding.py",
            "src/LibTmux.Query.Json/packages.packed.lock.json",
            "tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj",
            "tests/LibTmux.AotSmoke/packages.lock.json",
            "tests/LibTmux.AotSmoke/Program.cs",
            "tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj",
            "tests/LibTmux.PackageConsumer/packages.lock.json",
            "tests/LibTmux.PackageConsumer/Program.cs",
            "tests/LibTmux.PackageConsumer/NuGet.config",
            "tests/LibTmux.IntegrationTests/Packaging/PackageClosureTests.cs",
            "tests/LibTmux.UnitTests/Packaging/PublicApiContractTests.cs",
            "tests/LibTmux.UnitTests/Packaging/WorkflowContractTests.cs",
            "tests/LibTmux.IntegrationTests/Testing/TestingHelpersTests.cs",
            "examples/LibTmux.Examples/LibTmux.Examples.csproj",
            "examples/LibTmux.Examples/packages.lock.json",
            "examples/LibTmux.Examples/Program.cs",
        }
    ),
}
PUBLIC_API_FILE_BINDINGS: dict[str, tuple[int, str]] = {
    "T:LibTmux.Internal.CommandFlagCatalog": (
        3,
        "src/LibTmux/Internal/CommandFlagCatalog.cs",
    ),
    "T:LibTmux.Internal.FormatCatalog": (
        3,
        "src/LibTmux/Internal/FormatCatalog.cs",
    ),
    "T:LibTmux.Internal.FormatFieldDescriptor": (
        3,
        "src/LibTmux/Internal/FormatFieldDescriptor.cs",
    ),
    "T:LibTmux.OptionScope": (
        3,
        "src/LibTmux/Constants/TmuxEnums.cs",
    ),
    "T:LibTmux.PaneDirection": (
        3,
        "src/LibTmux/Constants/TmuxEnums.cs",
    ),
    "T:LibTmux.ResizeDirection": (
        3,
        "src/LibTmux/Constants/TmuxEnums.cs",
    ),
    "T:LibTmux.WindowDirection": (
        3,
        "src/LibTmux/Constants/TmuxEnums.cs",
    ),
    "T:LibTmux.Internal.SeparatedRowFramer": (
        4,
        "src/LibTmux/Materialization/SeparatedRowFramer.cs",
    ),
    "T:LibTmux.Internal.FormatProjection": (
        4,
        "src/LibTmux/Materialization/FormatProjection.cs",
    ),
    "T:LibTmux.Internal.MaterializationContext": (
        4,
        "src/LibTmux/Materialization/MaterializationContext.cs",
    ),
    "T:LibTmux.Internal.MaterializationQuery": (
        4,
        "src/LibTmux/Materialization/TmuxMaterializationQuery.cs",
    ),
    "T:LibTmux.Internal.Materializer": (
        4,
        "src/LibTmux/Materialization/TmuxMaterializer.cs",
    ),
    "T:LibTmux.Internal.ServerProjection": (
        4,
        "src/LibTmux/Materialization/FormatProjection.cs",
    ),
    "T:LibTmux.Internal.ServerProjectionDescriptor": (
        4,
        "src/LibTmux/Materialization/FormatProjection.cs",
    ),
    "T:LibTmux.TmuxColorMode": (
        2,
        "src/LibTmux/TmuxColorMode.cs",
    ),
    "T:LibTmux.AttachSessionRequest": (
        10,
        "src/LibTmux/Requests/AttachSessionRequest.cs",
    ),
    "T:LibTmux.DisplayMessageRequest": (
        11,
        "src/LibTmux/Requests/DisplayMessageRequest.cs",
    ),
    "T:LibTmux.DisplayPopupRequest": (
        12,
        "src/LibTmux/Requests/DisplayPopupRequest.cs",
    ),
}
PUBLIC_API_MEMBER_FILE_BINDINGS: dict[str, tuple[int, str]] = {
    "P:LibTmux.Server.Version": (
        3,
        "src/LibTmux/Server.Version.cs",
    ),
}
FORMAT_SEPARATOR_CONTRACT: dict[str, t.Any] = {
    "componentId": 4,
    "destinationStatus": "excluded",
    "csharpDestination": None,
    "replacement": ("M:LibTmux.Internal.SeparatedRowFramer.Decode(ReadOnlySpan<byte>)"),
    "exclusionReason": (
        "Delimiter-based row framing is replaced by the raw-byte protocol approved "
        "in ADR 0001."
    ),
    "testPath": (
        "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs"
    ),
}
FORBIDDEN_PRODUCTION_FILES = frozenset(
    {
        "src/LibTmux/Materialization/FieldCatalog.cs",
        "src/LibTmux/Requests/AttachClientRequest.cs",
        "src/LibTmux/Requests/DisplayOverlayRequest.cs",
        "src/LibTmux/Internal/XunitTmuxHarness.cs",
    }
)
CORE_RESTORE_PAIR = (
    "mise exec -- dotnet restore src/LibTmux/LibTmux.csproj",
    "mise exec -- dotnet restore src/LibTmux/LibTmux.csproj --locked-mode",
)
JSON_DEFAULT_RESTORE_PAIR = (
    "mise exec -- dotnet restore src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
    "mise exec -- dotnet restore src/LibTmux.Query.Json/LibTmux.Query.Json.csproj --locked-mode",
)
JSON_PACKED_RESTORE_PAIR = (
    "mise exec -- dotnet restore src/LibTmux.Query.Json/LibTmux.Query.Json.csproj --source artifacts/packages --source https://api.nuget.org/v3/index.json -p:UsePackedLibTmux=true",
    "mise exec -- dotnet restore src/LibTmux.Query.Json/LibTmux.Query.Json.csproj --locked-mode --source artifacts/packages --source https://api.nuget.org/v3/index.json -p:UsePackedLibTmux=true",
)
LOCAL_FEED_SOLUTION_RESTORE_PAIR = (
    "mise exec -- dotnet restore LibTmux.slnx --source artifacts/packages --source https://api.nuget.org/v3/index.json",
    "mise exec -- dotnet restore LibTmux.slnx --locked-mode --source artifacts/packages --source https://api.nuget.org/v3/index.json",
)
PACKAGE_COMMANDS = (
    *CORE_RESTORE_PAIR,
    *JSON_DEFAULT_RESTORE_PAIR,
    "mise exec -- dotnet pack src/LibTmux/LibTmux.csproj --configuration Release --no-restore --output artifacts/packages -p:PackageVersion=0.1.0-local",
    *JSON_PACKED_RESTORE_PAIR,
    "mise exec -- dotnet pack src/LibTmux.Query.Json/LibTmux.Query.Json.csproj --configuration Release --no-restore --output artifacts/packages -p:PackageVersion=0.1.0-local -p:UsePackedLibTmux=true",
    *LOCAL_FEED_SOLUTION_RESTORE_PAIR,
    "unzip -l artifacts/packages/LibTmux.0.1.0-local.nupkg",
    "unzip -l artifacts/packages/LibTmux.0.1.0-local.snupkg",
    "unzip -l artifacts/packages/LibTmux.Query.Json.0.1.0-local.nupkg",
    "unzip -l artifacts/packages/LibTmux.Query.Json.0.1.0-local.snupkg",
    "unzip -p artifacts/packages/LibTmux.0.1.0-local.nupkg LibTmux.nuspec",
    "unzip -p artifacts/packages/LibTmux.Query.Json.0.1.0-local.nupkg LibTmux.Query.Json.nuspec",
    "uv run python eng/parity/inspect_packages.py --artifacts artifacts/packages --repository .",
)
PUBLIC_API_BUILD_COMMAND = "mise exec -- dotnet build LibTmux.slnx --configuration Release --no-restore --warnaserror"
PACKED_CONSUMER_COMMANDS = (
    "mise exec -- dotnet run --project tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj --configuration Release --framework net8.0 --no-build",
    "mise exec -- dotnet run --project tests/LibTmux.PackageConsumer/LibTmux.PackageConsumer.csproj --configuration Release --framework net10.0 --no-build",
)
EXAMPLE_COMMANDS = (
    "mise exec -- dotnet run --project examples/LibTmux.Examples/LibTmux.Examples.csproj --configuration Release --framework net8.0 --no-build",
    "mise exec -- dotnet run --project examples/LibTmux.Examples/LibTmux.Examples.csproj --configuration Release --framework net10.0 --no-build",
)
AOT_RID_RESTORE_PAIR = (
    "mise exec -- dotnet restore tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj --runtime linux-x64 --source artifacts/packages --source https://api.nuget.org/v3/index.json",
    "mise exec -- dotnet restore tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj --locked-mode --runtime linux-x64 --source artifacts/packages --source https://api.nuget.org/v3/index.json",
)
AOT_COMMANDS = (
    *AOT_RID_RESTORE_PAIR,
    "mise exec -- dotnet publish tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj --configuration Release --framework net8.0 --runtime linux-x64 --self-contained --no-restore -p:PublishAot=true -p:PublishTrimmed=true --output artifacts/aot/net8.0",
    "mise exec -- dotnet publish tests/LibTmux.AotSmoke/LibTmux.AotSmoke.csproj --configuration Release --framework net10.0 --runtime linux-x64 --self-contained --no-restore -p:PublishAot=true -p:PublishTrimmed=true --output artifacts/aot/net10.0",
    "artifacts/aot/net8.0/LibTmux.AotSmoke",
    "artifacts/aot/net10.0/LibTmux.AotSmoke",
)
C18_RESTORE_PAIRS = (
    CORE_RESTORE_PAIR,
    JSON_DEFAULT_RESTORE_PAIR,
    JSON_PACKED_RESTORE_PAIR,
    LOCAL_FEED_SOLUTION_RESTORE_PAIR,
    AOT_RID_RESTORE_PAIR,
)
WORKFLOW_CONFIGURATION_COMMANDS = (
    "uv run python eng/parity/verify_workflows.py --lane platform .github/workflows/dotnet.yml",
    "uv run python eng/parity/verify_workflows.py --lane macos-tmux .github/workflows/dotnet-tmux.yml",
)
FINAL_EVIDENCE_ROOT = "docs/parity/evidence/final"
C3_EVIDENCE_ROOT = "docs/parity/evidence/0001"
VERSION_DELTA_PATH = "docs/parity/version-deltas.json"
NON_RETAINED_MATRIX_COMMAND = "eng/tmux/run-matrix.sh tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj"
RETAINED_MATRIX_COMMAND = "eng/tmux/run-matrix.sh --capability-cohort closure --evidence-dir docs/parity/evidence/final tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj"
VALIDATE_FINAL_MATRIX_COMMAND = "uv run python eng/evidence/validate.py --phase matrix docs/parity/evidence/final"
PRECOMMIT_SOURCE_BINDING_COMMAND = "uv run python eng/evidence/verify_source_binding.py --evidence docs/parity/evidence/final --repository . --require-evaluated-commit HEAD --allow-dirty-root docs/parity/evidence/final --fingerprint-mode evaluated-commit-tree"
POSTCOMMIT_SOURCE_BINDING_COMMAND = "uv run python eng/evidence/verify_source_binding.py --evidence docs/parity/evidence/final --repository . --require-evaluated-commit HEAD^ --require-descendant-root docs/parity/evidence/final --require-descendant-path docs/parity/version-deltas.json --fingerprint-mode evaluated-commit-tree"
FINAL_RECONCILE_COMMAND = "uv run python eng/parity/reconcile_versions.py --evidence docs/parity/evidence/final/results.ndjson --write"
PERSISTED_RECONCILE_COMMAND = "uv run python eng/parity/reconcile_versions.py"
EVIDENCE_STAGE_COMMAND = "git add -- docs/parity/evidence/final docs/parity/version-deltas.json"
EVIDENCE_SCOPE_COMMAND = "uv run python eng/parity/verify_production_plan.py --phase closure --verify-final-evidence-staged-scope docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
EVIDENCE_COMMIT_COMMAND = "printf '%s\\n' 'Evidence(docs[closure]): Close policy proof' '' 'why: Bind retained compatibility evidence and reconciled policy status to the source commit.' '' what: '- Record the clean source commit and source fingerprint.' '- Retain the required tmux and framework lanes.' '- Reconcile wrapper-policy evidence.' | git commit --file -"
SOURCE_WORKTREE_CLEAN_COMMAND = 'test -z "$(git status --porcelain)"'
FINAL_MATRIX_COMMANDS = (
    RETAINED_MATRIX_COMMAND,
    VALIDATE_FINAL_MATRIX_COMMAND,
    PRECOMMIT_SOURCE_BINDING_COMMAND,
    FINAL_RECONCILE_COMMAND,
    PERSISTED_RECONCILE_COMMAND,
    EVIDENCE_STAGE_COMMAND,
    EVIDENCE_SCOPE_COMMAND,
    "git diff --cached --check",
    EVIDENCE_COMMIT_COMMAND,
    POSTCOMMIT_SOURCE_BINDING_COMMAND,
    SOURCE_WORKTREE_CLEAN_COMMAND,
)
C3_RETAINED_MATRIX_COMMAND = "eng/tmux/run-matrix.sh --capability-cohort 0001 --evidence-dir docs/parity/evidence/0001 tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj"
C3_VALIDATE_MATRIX_COMMAND = "uv run python eng/evidence/validate.py --phase matrix docs/parity/evidence/0001"
C3_PRECOMMIT_SOURCE_BINDING_COMMAND = "uv run python eng/evidence/verify_source_binding.py --evidence docs/parity/evidence/0001 --repository . --require-evaluated-commit HEAD --allow-dirty-root docs/parity/evidence/0001 --fingerprint-mode evaluated-commit-tree"
C3_RECONCILE_COMMAND = "uv run python eng/parity/reconcile_versions.py --evidence docs/parity/evidence/0001/results.ndjson --write"
C3_EVIDENCE_STAGE_COMMAND = (
    "git add -- docs/parity/evidence/0001 docs/parity/version-deltas.json"
)
C3_EVIDENCE_SCOPE_COMMAND = "uv run python eng/parity/verify_production_plan.py --phase component --component 3 --verify-retained-evidence-staged-scope docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
C3_EVIDENCE_COMMIT_COMMAND = "printf '%s\\n' 'Evidence(docs[versioning]): Retain cohort 0001' '' 'why: Bind protocol evidence and reconciled observations to the Component 3 source commit.' '' what: '- Retain the exact protocol cohort.' '- Reconcile the five protocol observations.' | git commit --file -"
C3_POSTCOMMIT_SOURCE_BINDING_COMMAND = "uv run python eng/evidence/verify_source_binding.py --evidence docs/parity/evidence/0001 --repository . --require-evaluated-commit HEAD^ --require-descendant-root docs/parity/evidence/0001 --require-descendant-path docs/parity/version-deltas.json --fingerprint-mode evaluated-commit-tree"
C3_EVIDENCE_COMMANDS = (
    C3_RETAINED_MATRIX_COMMAND,
    C3_VALIDATE_MATRIX_COMMAND,
    C3_PRECOMMIT_SOURCE_BINDING_COMMAND,
    C3_RECONCILE_COMMAND,
    PERSISTED_RECONCILE_COMMAND,
    C3_EVIDENCE_STAGE_COMMAND,
    C3_EVIDENCE_SCOPE_COMMAND,
    "git diff --cached --check",
    C3_EVIDENCE_COMMIT_COMMAND,
    C3_POSTCOMMIT_SOURCE_BINDING_COMMAND,
    SOURCE_WORKTREE_CLEAN_COMMAND,
)
EVIDENCE_CLOSURE_TAILS: dict[int, tuple[str, ...]] = {
    3: (SOURCE_WORKTREE_CLEAN_COMMAND, *C3_EVIDENCE_COMMANDS),
    18: (SOURCE_WORKTREE_CLEAN_COMMAND, *FINAL_MATRIX_COMMANDS),
}
RETAINED_EVIDENCE_SCOPES: dict[int, tuple[str, ...]] = {
    3: (C3_EVIDENCE_ROOT, VERSION_DELTA_PATH),
}
ROOT_QUALITY_COMMANDS = (
    "uv run ruff format --check .",
    "uv run ruff check .",
    "uv run mypy",
    "uv run mypy eng/parity",
    "uv run mypy eng/evidence",
    "uv run pytest --doctest-modules",
    "just build-docs",
)
FORBIDDEN_ROOT_QUALITY_COMMANDS = frozenset({"uv run mypy ."})
PUBLICATION_PROVENANCE_COMMANDS = (
    "git branch --show-current",
    "git rev-parse HEAD",
    "git tag --points-at HEAD",
    "git status --short --branch",
)
STAGED_PATH_ERROR = "staged paths cannot be inspected"
REQUIRED_GATE_COMMANDS: dict[int, tuple[str, ...]] = {
    3: (
        NON_RETAINED_MATRIX_COMMAND,
        *C3_EVIDENCE_COMMANDS[:-1],
    ),
    18: (
        *PACKAGE_COMMANDS,
        PUBLIC_API_BUILD_COMMAND,
        *PACKED_CONSUMER_COMMANDS,
        *EXAMPLE_COMMANDS,
        *AOT_COMMANDS,
        *WORKFLOW_CONFIGURATION_COMMANDS,
        NON_RETAINED_MATRIX_COMMAND,
        *(
            command
            for command in FINAL_MATRIX_COMMANDS
            if command
            not in {"git diff --cached --check", SOURCE_WORKTREE_CLEAN_COMMAND}
        ),
    ),
}
REQUIRED_CLOSURE_COMMANDS: dict[str, tuple[str, ...]] = {
    "Package": PACKAGE_COMMANDS,
    "Public API": (
        PUBLIC_API_BUILD_COMMAND,
        "uv run python eng/parity/verify_production_plan.py --phase closure docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md",
    ),
    "Repository quality": ROOT_QUALITY_COMMANDS,
    "Diff integrity": ("git diff --check",),
    "Clean worktree": ('test -z "$(git status --porcelain)"',),
    "Publication boundary": PUBLICATION_PROVENANCE_COMMANDS,
    "Platform workflow configuration": (WORKFLOW_CONFIGURATION_COMMANDS[0],),
    "macOS tmux workflow configuration": (WORKFLOW_CONFIGURATION_COMMANDS[1],),
    "Packed consumers": PACKED_CONSUMER_COMMANDS,
    "Executable examples": EXAMPLE_COMMANDS,
    "NativeAOT": AOT_COMMANDS,
    "Final matrix evidence": (
        VALIDATE_FINAL_MATRIX_COMMAND,
        POSTCOMMIT_SOURCE_BINDING_COMMAND,
    ),
}
REQUIRED_RED_TESTS: dict[int, tuple[str, ...]] = {
    1: (
        "EntityShellTests.Canonical_entities_are_public_sealed_partial_before_members_are_added",
        "TmuxProcessTransportTests.Preserves_raw_bytes_and_projects_universal_newlines",
        "TmuxProcessTransportTests.Treats_public_semicolon_as_data_and_internal_typed_separator_as_structure",
        "TmuxProcessTransportTests.Defensively_copies_logical_arguments_and_uses_deep_record_equality",
        "TmuxProcessTransportTests.Enforces_transport_limits_and_bounded_cleanup",
        "TmuxProcessTransportTests.ThrowIfFailed_observes_projected_stderr_without_mutating_raw_bytes",
        "TmuxProcessTransportTests.Injects_launcher_clock_and_limits_without_wall_clock_sleeps",
        "TmuxProcessTransportTests.Missing_binary_throws_TmuxCommandNotFoundException_with_configured_path",
        "TmuxProcessTransportTests.Pre_start_cancellation_throws_OperationCanceledException_with_caller_token_without_starting_process",
        "TmuxProcessTransportTests.Post_start_cancellation_throws_TmuxOperationCanceledException_with_true_execution_risk_and_client_pid",
        "TmuxProcessTransportTests.Cleanup_failure_throws_TmuxCleanupException_with_original_context",
        "TmuxProcessTransportTests.Invalid_utf8_projects_each_bad_byte_as_lowercase_hex_escape",
        "ProcessTransportTests.Pty_attached_client_scope_uses_real_pty",
        "ProcessTransportTests.Test_child_preserves_concurrent_raw_stdout_and_stderr",
        "ProcessTransportTests.Test_child_preserves_invalid_bytes",
        "ProcessTransportTests.Test_child_projects_partial_final_output",
        "ProcessTransportTests.Test_child_returns_nonzero_exit",
        "ProcessTransportTests.Test_child_bounds_a_held_pump",
        "ProcessTransportTests.Post_start_cancellation_reaps_client_but_leaves_descendant_alive",
        "ProcessTransportTests.Test_child_reports_cleanup_faults",
        "RequireRedTests.Accepts_only_nonzero_run_with_exact_failed_selected_test",
        "RequireRedTests.Rejects_build_or_discovery_failure",
        "RequireRedTests.Rejects_zero_tests_and_all_skipped_tests",
        "RequireRedTests.Rejects_aborted_or_canceled_runs",
        "RequireRedTests.Rejects_malformed_or_missing_trx",
        "RequireRedTests.Rejects_unexpected_test_identity",
        "RequireRedTests.Rejects_successful_test_run",
        "RequireRedTests.Rejects_stale_exact_failed_trx_after_build_or_discovery_failure",
    ),
    3: (
        "TmuxCapabilitiesTests.Comparisons_cover_equal_older_and_newer_versions",
        "VersionParityTests.AttachmentAccounting",
        "VersionParityTests.BreakPane37Workaround",
        "VersionParityTests.ByteLengthFraming",
        "VersionParityTests.CapturePane37Metadata",
        "VersionParityTests.CapturePaneModeScreen",
        "VersionParityTests.CapturePaneTrimTrailing",
        "VersionParityTests.ChooseTreeSortTime",
        "VersionParityTests.ClearHistoryHyperlinks",
        "VersionParityTests.ClearPromptHistoryCommand",
        "VersionParityTests.CommandPrompt37Behavior",
        "VersionParityTests.CommandPromptBackground",
        "VersionParityTests.CommandPromptLiteral",
        "VersionParityTests.ConfirmBeforeAcceptance",
        "VersionParityTests.ConfirmBeforeBackground",
        "VersionParityTests.ControlNotifications",
        "VersionParityTests.CopyModePageDown",
        "VersionParityTests.DisplayMenuMouse",
        "VersionParityTests.DisplayMenuStyles",
        "VersionParityTests.DisplayMessageClient",
        "VersionParityTests.DisplayMessageLiteral",
        "VersionParityTests.DisplayMessageUpdatePane",
        "VersionParityTests.DisplayPopup33Options",
        "VersionParityTests.DisplayPopup36KeyPolicy",
        "VersionParityTests.FormatFieldsAndOperators",
        "VersionParityTests.HookScopePaneWindowSet",
        "VersionParityTests.HookScopePaneWindowShow",
        "VersionParityTests.KillSessionGroup",
        "VersionParityTests.ListKeysFormat",
        "VersionParityTests.NewPaneCommand",
        "VersionParityTests.OptionDollarDoubleEscape",
        "VersionParityTests.PasteBufferNoVis",
        "VersionParityTests.RefreshClientClipboardQuery",
        "VersionParityTests.RunShellArguments",
        "VersionParityTests.RunShellShowStderr",
        "VersionParityTests.RunShellWorkingDirectory",
        "VersionParityTests.SemicolonGrouping",
        "VersionParityTests.SendKeysClientKeys",
        "VersionParityTests.ServerAccessCommand",
        "VersionParityTests.ShowPromptHistoryCommand",
        "VersionParityTests.SplitWindowAppearance",
        "VersionParityTests.SplitWindowEmpty",
        "VersionParityTests.CommandFlags",
    ),
    4: (
        "MaterializationTests.Format_separator_exclusion_uses_single_expansion_decode",
        "MaterializationTests.Materializer_uses_server_context_and_returns_typed_raw_fields",
        "MaterializationTests.Generated_projection_round_trips_multiple_hostile_rows",
        "MaterializationTests.Version_gates_emit_only_supported_fields",
        "MaterializationTests.Window_and_pane_lookup_use_tmux_canonical_session",
        "MaterializationTests.Missing_target_is_distinct_from_unreachable_server",
    ),
    7: (
        "SnapshotCollectionTests.List_accessors_are_lenient_on_tmux_errors",
        "SnapshotCollectionTests.Explicit_liveness_checks_preserve_failures",
    ),
    8: (
        "QuerySemanticsTests.And_and_or_nodes_use_ordered_structural_equality_and_hashing",
    ),
    10: (
        "ServerSessionLifecycleTests.New_session_flags_emit_exact_argv",
        "ServerSessionLifecycleTests.Session_selection_and_attachment_flags_emit_exact_argv",
        "ServerSessionLifecycleTests.Refresh_after_external_selection_captures_active_window_and_pane_relations",
    ),
    11: (
        "WindowTopologyTests.New_split_move_link_swap_resize_rotate_and_respawn_flags_emit_exact_argv",
        "WindowTopologyTests.Killed_window_is_a_raising_tombstone",
    ),
    12: (
        "PaneOperationsTests.Capture_flags_emit_exact_argv_and_preserve_positions",
        "PaneOperationsTests.Send_keys_flags_distinguish_literal_and_key_modes",
        "PaneOperationsTests.Select_direction_last_keep_zoom_mark_and_input_flags_emit_exact_argv",
        "PaneOperationsTests.New_split_move_join_paste_display_clear_and_break_flags_emit_exact_argv",
        "PaneOperationsTests.Copy_find_pipe_swap_resize_and_respawn_flags_emit_exact_argv",
        "PaneOperationsTests.Popup_menu_and_display_flags_emit_exact_argv",
    ),
    13: (
        "ClientAdministrationTests.Attach_switch_detach_lock_and_suspend_flags_emit_exact_argv",
    ),
    14: (
        "TmuxOptionsTests.Global_inherited_and_unset_scopes_emit_exact_flags",
        "TmuxOptionsTests.Sparse_arrays_and_raw_values_round_trip",
        "TmuxOptionsTests.Invalid_ambiguous_and_unknown_options_map_to_typed_failures",
        "TmuxOptionsTests.Window_option_aliases_resolve_to_the_window_scope",
    ),
    15: (
        "HookOperationsTests.Set_show_unset_and_run_flags_emit_exact_argv",
        "EnvironmentOperationsTests.Set_show_unset_and_remove_flags_emit_exact_argv",
    ),
    16: (
        "ServerUtilitiesTests.Bind_unbind_and_list_key_flags_emit_exact_argv",
        "ServerUtilitiesTests.Prompt_menu_confirm_and_display_flags_emit_exact_argv",
        "ServerUtilitiesTests.Buffer_flags_emit_exact_argv",
        "ServerUtilitiesTests.Shell_if_source_wait_and_access_flags_emit_exact_argv",
    ),
    17: (
        "ExceptionContractTests.Command_specific_errors_preserve_typed_context",
        "ExceptionContractTests.Cancellation_and_cleanup_failures_preserve_distinct_state",
        "ExceptionContractTests.Excluded_python_exceptions_have_exact_replacements",
    ),
    18: (
        "PackageClosureTests.Packed_metadata_dependencies_and_assets_are_exact",
        "PackageClosureTests.SourceLink_repository_revision_and_privacy_are_exact",
        "PackageClosureTests.Packed_consumers_execute_on_both_frameworks",
        "PackageClosureTests.Documented_examples_execute_against_real_tmux",
        "PackageClosureTests.Trimmed_native_aot_executes_on_both_frameworks",
        "WorkflowContractTests.Platform_and_macos_tmux_configurations_are_exact",
        "PublicApiContractTests.Shipped_baselines_match_both_packages",
        "SourceBindingTests.Final_matrix_matches_the_closing_source_tree",
    ),
}
RED_CASES: dict[int, tuple[str, str]] = {
    1: (
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        "TmuxProcessTransportTests.Preserves_raw_bytes_and_projects_universal_newlines",
    ),
    2: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "ServerGenerationTests.Stale_entity_cannot_target_a_reused_id",
    ),
    3: (
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        "TmuxCapabilitiesTests.Comparisons_cover_equal_older_and_newer_versions",
    ),
    4: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "MaterializationTests.Materializes_embedded_newlines_and_invalid_utf8",
    ),
    5: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "HierarchySnapshotTests.Linked_windows_preserve_edges_without_losing_entity_identity",
    ),
    6: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "ChildEnvironmentTests.Starting_server_removes_inherited_tmux_without_mutating_process_environment",
    ),
    7: (
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        "SnapshotCollectionTests.Enumeration_is_local_and_uses_BCL_cardinality",
    ),
    8: (
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        "QuerySemanticsTests.Matching_translates_and_interprets_the_canonical_AST",
    ),
    9: (
        "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
        "QueryJsonTests.Round_trips_every_version_one_golden_byte_for_byte",
    ),
    10: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "ServerSessionLifecycleTests.Refresh_returns_replacement_and_owned_scope_cleans_up",
    ),
    11: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "WindowTopologyTests.Linked_window_moves_preserve_session_scoped_indexes",
    ),
    12: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "PaneOperationsTests.Send_keys_and_capture_preserve_literal_payloads",
    ),
    13: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "ClientAdministrationTests.Detached_client_resolves_nullable_attachment",
    ),
    14: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "TmuxOptionsTests.Preserves_global_inherited_sparse_and_raw_values",
    ),
    15: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "HookOperationsTests.Server_and_session_hooks_round_trip_without_global_state",
    ),
    16: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "ServerUtilitiesTests.Keys_prompts_menus_buffers_and_shell_commands_use_exact_argv",
    ),
    17: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "StructuredLoggingTests.Records_stable_scalar_context_without_payload_leakage",
    ),
    18: (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
        "TestingHelpersTests.Temporary_hierarchy_is_xunit_independent_and_cleans_up",
    ),
}
RED_EVIDENCE: dict[int, str] = {
    component: f"artifacts/tdd/component-{component:02d}.trx"
    for component in COMPONENT_IDS
}
RED_TEST_NAMESPACES: dict[int, str] = {
    1: "LibTmux.UnitTests.Transport",
    2: "LibTmux.IntegrationTests.Connection",
    3: "LibTmux.UnitTests.Versioning",
    4: "LibTmux.IntegrationTests.Materialization",
    5: "LibTmux.IntegrationTests.Snapshots",
    6: "LibTmux.IntegrationTests.Environment",
    7: "LibTmux.UnitTests.Collections",
    8: "LibTmux.UnitTests.Query",
    9: "LibTmux.UnitTests.Query",
    10: "LibTmux.IntegrationTests.Hierarchy",
    11: "LibTmux.IntegrationTests.Hierarchy",
    12: "LibTmux.IntegrationTests.Hierarchy",
    13: "LibTmux.IntegrationTests.Clients",
    14: "LibTmux.IntegrationTests.Options",
    15: "LibTmux.IntegrationTests.Hooks",
    16: "LibTmux.IntegrationTests.Utilities",
    17: "LibTmux.IntegrationTests.Diagnostics",
    18: "LibTmux.IntegrationTests.Testing",
}
RED_TEST_IDENTITIES: dict[int, str] = {
    component: f"{RED_TEST_NAMESPACES[component]}.{test_name}"
    for component, (_, test_name) in RED_CASES.items()
}
RED_COMMANDS: dict[int, str] = {
    component: (
        "uv run python eng/parity/require_red.py "
        f"--project {project} --configuration Release --framework net8.0 "
        f"--no-restore --test {RED_TEST_IDENTITIES[component]} "
        f"--evidence {RED_EVIDENCE[component]}"
    )
    for component, (project, _) in RED_CASES.items()
}


class StagedScopeError(RuntimeError):
    """Git could not inspect the component's staged scope."""


def load_ledger(path: pathlib.Path = LEDGER_PATH) -> dict[str, t.Any]:
    """Load the approved parity ledger.

    Parameters
    ----------
    path : pathlib.Path
        Ledger path.

    Returns
    -------
    dict[str, typing.Any]
        Parsed ledger document.

    Examples
    --------
    >>> "rows" in load_ledger()
    True
    """
    with path.open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def parse_markdown(
    markdown: str,
) -> tuple[list[dict[str, t.Any]], dict[str, list[list[str]]]]:
    r"""Parse component tasks and closure fields from Markdown headings.

    Parameters
    ----------
    markdown : str
        Production plan Markdown.

    Returns
    -------
    tuple[list[dict[str, typing.Any]], dict[str, list[list[str]]]]
        Parsed component sections and closure fields.

    Examples
    --------
    >>> sections, closure = parse_markdown("## Component 1: One\n### Files\n- `a`\n")
    >>> sections[0]["id"], closure
    (1, {})
    """
    components: list[dict[str, t.Any]] = []
    closure: dict[str, list[list[str]]] = collections.defaultdict(list)
    current_component: dict[str, t.Any] | None = None
    current_fields: dict[str, list[list[str]]] | None = None
    current_block: list[str] | None = None
    in_closure = False

    for line in markdown.splitlines():
        component_match = COMPONENT_RE.fullmatch(line)
        if component_match:
            current_component = {
                "id": int(component_match.group(1)),
                "fields": collections.defaultdict(list),
            }
            components.append(current_component)
            current_fields = t.cast(
                dict[str, list[list[str]]], current_component["fields"]
            )
            current_block = None
            in_closure = False
            continue
        if line == "## Closure":
            current_component = None
            current_fields = closure
            current_block = None
            in_closure = True
            continue
        if line.startswith("## "):
            current_component = None
            current_fields = None
            current_block = None
            in_closure = False
            continue
        field_match = FIELD_RE.fullmatch(line)
        if field_match and current_fields is not None:
            field_name = field_match.group(1)
            if in_closure and field_name.endswith(" gate"):
                field_name = field_name.removesuffix(" gate")
            current_block = []
            current_fields[field_name].append(current_block)
            continue
        if current_block is not None:
            current_block.append(line)

    return components, dict(closure)


def nonblank(block: list[str]) -> list[str]:
    """Return nonblank lines from a Markdown field.

    Parameters
    ----------
    block : list[str]
        Field lines.

    Returns
    -------
    list[str]
        Nonblank lines.

    Examples
    --------
    >>> nonblank(["", "value", ""])
    ['value']
    """
    return [line for line in block if line.strip()]


def planned_commit_command(block: list[str]) -> str | None:
    """Build the exact shell command for one declared atomic commit.

    Parameters
    ----------
    block : list[str]
        Atomic commit field.

    Returns
    -------
    str | None
        Exact command, or ``None`` for an invalid field.

    Examples
    --------
    >>> planned_commit_command(["`Scope(feat): Add behavior`", "", "why: Needed.", "", "what:", "- Add it."]).endswith("| git commit --file -")
    True
    """
    lines = nonblank(block)
    if not lines or re.fullmatch(r"`[^`\n]+`", lines[0]) is None:
        return None
    why = [line for line in lines[1:] if line.startswith("why:")]
    what_indexes = [index for index, line in enumerate(lines) if line == "what:"]
    if len(why) != 1 or len(what_indexes) != 1:
        return None
    bullets = [line for line in lines[what_indexes[0] + 1 :] if line.startswith("- ")]
    if not bullets:
        return None
    message = (lines[0][1:-1], "", why[0], "", "what:", *bullets)
    return (
        "printf '%s\\n' "
        + " ".join(shlex.quote(line) for line in message)
        + " | git commit --file -"
    )


def list_tokens(block: list[str]) -> list[str] | None:
    """Parse a field made only from backtick-wrapped list items.

    Parameters
    ----------
    block : list[str]
        Field lines.

    Returns
    -------
    list[str] | None
        Tokens, or ``None`` when the field is malformed.

    Examples
    --------
    >>> list_tokens(["", "- `one`", "- `two`"])
    ['one', 'two']
    >>> list_tokens(["plain"]) is None
    True
    """
    lines = nonblank(block)
    matches = [LIST_TOKEN_RE.fullmatch(line) for line in lines]
    if not lines or any(match is None for match in matches):
        return None
    return [t.cast(re.Match[str], match).group("token") for match in matches]


def markdown_commands(markdown: str) -> list[str]:
    """Return every backtick-wrapped Markdown list token.

    Parameters
    ----------
    markdown : str
        Production plan Markdown.

    Returns
    -------
    list[str]
        Tokens that may represent executable commands.
    """
    return [
        match.group("token")
        for line in markdown.splitlines()
        if (match := LIST_TOKEN_RE.fullmatch(line)) is not None
    ]


def one_field(
    component: dict[str, t.Any],
    name: str,
    violations: list[str],
) -> list[str] | None:
    """Return one required component field and report cardinality errors.

    Parameters
    ----------
    component : dict[str, typing.Any]
        Parsed component.
    name : str
        Field name.
    violations : list[str]
        Violation accumulator.

    Returns
    -------
    list[str] | None
        The unique field block, when present.

    Examples
    --------
    >>> errors: list[str] = []
    >>> one_field({"id": 1, "fields": {}}, "Files", errors) is None
    True
    >>> errors
    ['component 1 missing Files']
    """
    blocks = t.cast(dict[str, list[list[str]]], component["fields"]).get(name, [])
    if not blocks:
        violations.append(f"component {component['id']} missing {name}")
        return None
    if len(blocks) != 1:
        violations.append(f"component {component['id']} has duplicate {name}")
        return None
    return blocks[0]


def validate_component(
    component: dict[str, t.Any],
    row_owners: dict[str, list[int]],
    violations: list[str],
    component_files: dict[int, set[str]] | None = None,
) -> None:
    """Validate one structurally parsed component task.

    Parameters
    ----------
    component : dict[str, typing.Any]
        Parsed task.
    row_owners : dict[str, list[int]]
        Ledger-row ownership accumulator.
    violations : list[str]
        Violation accumulator.
    component_files : dict[int, set[str]] | None
        Exact file ownership accumulator.

    Examples
    --------
    >>> errors: list[str] = []
    >>> validate_component({"id": 1, "fields": {}}, {}, errors)
    >>> errors[0]
    'component 1 missing Files'
    """
    blocks = {name: one_field(component, name, violations) for name in REQUIRED_FIELDS}
    component_id = t.cast(int, component["id"])

    files = list_tokens(blocks["Files"]) if blocks["Files"] is not None else None
    if files is not None and component_files is not None:
        component_files[component_id] = set(files)
    if blocks["Files"] is not None and (
        files is None
        or any(
            not EXACT_PATH_RE.fullmatch(path)
            or any(character in path for character in "*?[]{}")
            for path in files
        )
    ):
        violations.append(f"component {component_id} has non-exact Files")
    if blocks["Files"] is not None and tuple(files or ()) != COMPONENT_FILES.get(
        component_id, ()
    ):
        violations.append(f"component {component_id} has invalid Files inventory")
    if (
        component_id == 1
        and files is not None
        and not set(files).issuperset(BUILD_BOOTSTRAP_FILES)
    ):
        violations.append("component 1 missing build bootstrap Files")
    if (
        component_id == 1
        and files is not None
        and not set(files).issuperset(FOUNDATIONAL_FILES)
    ):
        violations.append("component 1 missing foundational Files")
    if files is not None and not set(files).issuperset(
        REQUIRED_PROJECT_FILES.get(component_id, frozenset())
    ):
        violations.append(f"component {component_id} missing required project Files")
    if files is not None and not set(files).issuperset(
        ENTITY_FRAGMENT_FILES.get(component_id, ())
    ):
        violations.append(f"component {component_id} missing entity partial Files")
    if files is not None and len(files) != len(set(files)):
        violations.append("Files path has multiple component owners")

    api_owners = (
        list_tokens(blocks["API owners"]) if blocks["API owners"] is not None else None
    )
    if blocks["API owners"] is not None and tuple(api_owners or ()) != (
        COMPONENT_API_TYPES.get(component_id, ())
    ):
        violations.append(f"component {component_id} has invalid API owners")

    shared_files = (
        list_tokens(blocks["Shared files"])
        if blocks["Shared files"] is not None
        else None
    )
    if blocks["Shared files"] is not None and (
        shared_files is None
        or len(shared_files) != len(set(shared_files))
        or any(
            not EXACT_PATH_RE.fullmatch(path)
            or any(character in path for character in "*?[]{}")
            for path in shared_files
        )
    ):
        violations.append(f"component {component_id} has non-exact Shared files")
    if blocks["Shared files"] is not None and tuple(shared_files or ()) != (
        COMPONENT_SHARED_FILES.get(component_id, ())
    ):
        violations.append(f"component {component_id} has invalid Shared files")

    dependencies = (
        list_tokens(blocks["Depends on"]) if blocks["Depends on"] is not None else None
    )
    if blocks["Depends on"] is not None and tuple(dependencies or ()) != (
        COMPONENT_DEPENDENCIES.get(component_id, ())
    ):
        violations.append(f"component {component_id} has invalid Depends on")

    project_wiring = (
        list_tokens(blocks["Project wiring"])
        if blocks["Project wiring"] is not None
        else None
    )
    if blocks["Project wiring"] is not None and tuple(project_wiring or ()) != (
        PROJECT_WIRING.get(component_id, ("not applicable",))
    ):
        violations.append(f"component {component_id} has invalid Project wiring")
    if component_id == 18 and (
        files is None
        or "src/LibTmux.Query.Json/packages.packed.lock.json" not in files
        or project_wiring is None
        or tuple(project_wiring) != PROJECT_WIRING[18]
    ):
        violations.append("component 18 missing packed Query.Json lock graph")

    transport_contract_blocks = t.cast(
        dict[str, list[list[str]]], component["fields"]
    ).get("Transport contract", [])
    transport_contract = (
        list_tokens(transport_contract_blocks[0])
        if len(transport_contract_blocks) == 1
        else None
    )
    if component_id == 1 and tuple(transport_contract or ()) != (
        COMPONENT_ONE_TRANSPORT_CONTRACT
    ):
        violations.append("component 1 missing frozen transport contract")
    if component_id != 1 and transport_contract_blocks:
        violations.append(f"component {component_id} has unexpected transport contract")

    red_runner_blocks = t.cast(dict[str, list[list[str]]], component["fields"]).get(
        "RED runner contract", []
    )
    red_runner_contract = (
        list_tokens(red_runner_blocks[0]) if len(red_runner_blocks) == 1 else None
    )
    if component_id == 1 and tuple(red_runner_contract or ()) != RED_RUNNER_CONTRACT:
        violations.append("component 1 missing frozen RED runner contract")
    if component_id != 1 and red_runner_blocks:
        violations.append(
            f"component {component_id} has unexpected RED runner contract"
        )

    freshness_blocks = t.cast(dict[str, list[list[str]]], component["fields"]).get(
        "RED evidence freshness", []
    )
    freshness_contract = (
        list_tokens(freshness_blocks[0]) if len(freshness_blocks) == 1 else None
    )
    if component_id == 1 and tuple(freshness_contract or ()) != (
        RED_EVIDENCE_FRESHNESS_CONTRACT
    ):
        violations.append("component 1 missing fresh RED evidence contract")
    if component_id != 1 and freshness_blocks:
        violations.append(
            f"component {component_id} has unexpected RED evidence freshness"
        )

    tmux_37_transition_blocks = t.cast(
        dict[str, list[list[str]]], component["fields"]
    ).get("tmux 3.7 transition proof", [])
    tmux_37_transition_proof = (
        list_tokens(tmux_37_transition_blocks[0])
        if len(tmux_37_transition_blocks) == 1
        else None
    )
    if component_id == 3 and tuple(tmux_37_transition_proof or ()) != (
        TMUX_37_TRANSITION_PROOF_CONTRACT
    ):
        violations.append("component 3 has invalid tmux 3.7 transition proof")
    if component_id != 3 and tmux_37_transition_blocks:
        violations.append(
            f"component {component_id} has unexpected tmux 3.7 transition proof"
        )

    policy_proof_blocks = t.cast(dict[str, list[list[str]]], component["fields"]).get(
        "Version policy proofs", []
    )
    policy_proofs = (
        list_tokens(policy_proof_blocks[0]) if len(policy_proof_blocks) == 1 else None
    )
    expected_policy_proofs = VERSION_POLICY_PROOFS_BY_COMPONENT.get(component_id)
    if expected_policy_proofs is not None and tuple(policy_proofs or ()) != (
        expected_policy_proofs
    ):
        violations.append(f"component {component_id} has invalid version policy proofs")
    if expected_policy_proofs is None and policy_proof_blocks:
        violations.append(
            f"component {component_id} has unexpected version policy proofs"
        )

    materialization_contract_blocks = t.cast(
        dict[str, list[list[str]]], component["fields"]
    ).get("Materialization contract", [])
    materialization_contract = (
        list_tokens(materialization_contract_blocks[0])
        if len(materialization_contract_blocks) == 1
        else None
    )
    if component_id == 4 and tuple(materialization_contract or ()) != (
        C4_MATERIALIZATION_CONTRACT
    ):
        violations.append("component 4 has invalid materialization contract")
    if component_id != 4 and materialization_contract_blocks:
        violations.append(
            f"component {component_id} has unexpected materialization contract"
        )

    failure_corpus_blocks = t.cast(dict[str, list[list[str]]], component["fields"]).get(
        "Failure corpus contract", []
    )
    failure_corpus = (
        list_tokens(failure_corpus_blocks[0])
        if len(failure_corpus_blocks) == 1
        else None
    )
    if component_id == 1 and tuple(failure_corpus or ()) != (
        C1_FAILURE_CORPUS_CONTRACT
    ):
        violations.append("component 1 missing frozen failure corpus")
    if component_id != 1 and failure_corpus_blocks:
        violations.append(
            f"component {component_id} has unexpected failure corpus contract"
        )

    red_bootstrap_blocks = t.cast(dict[str, list[list[str]]], component["fields"]).get(
        "RED bootstrap", []
    )
    red_bootstrap = (
        list_tokens(red_bootstrap_blocks[0]) if len(red_bootstrap_blocks) == 1 else None
    )
    expected_red_bootstrap = RED_BOOTSTRAP.get(component_id)
    if expected_red_bootstrap is not None and tuple(red_bootstrap or ()) != (
        expected_red_bootstrap
    ):
        violations.append(f"component {component_id} has invalid RED bootstrap")
    if expected_red_bootstrap is None and red_bootstrap_blocks:
        violations.append(f"component {component_id} has unexpected RED bootstrap")

    rows = (
        list_tokens(blocks["Ledger rows"])
        if blocks["Ledger rows"] is not None
        else None
    )
    if rows is not None:
        for row_id in rows:
            row_owners.setdefault(row_id, []).append(component_id)
    elif blocks["Ledger rows"] is not None:
        violations.append(f"component {component_id} has invalid Ledger rows")

    red = nonblank(blocks["Red behavioral test"] or [])
    if blocks["Red behavioral test"] is not None and (
        not red
        or not any("`" in line for line in red)
        or not any("fail" in line.lower() for line in red)
    ):
        violations.append(f"component {component_id} has invalid Red behavioral test")
    required_red_tests = REQUIRED_RED_TESTS.get(component_id, ())
    red_content = "\n".join(red)
    if blocks["Red behavioral test"] is not None and any(
        f"`{test_name}`" not in red_content for test_name in required_red_tests
    ):
        violations.append(
            f"component {component_id} missing required Red behavioral tests"
        )
    violations.extend(
        f"component {component_id} declares Red behavioral test "
        f"{test_name}, which no test file defines"
        for test_name in required_red_tests
        if not defines_test(test_name)
    )
    if component_id == 3 and tuple(re.findall(r"`([^`]+)`", red_content)) != (
        required_red_tests
    ):
        violations.append("component 3 has invalid Red behavioral tests")
    if component_id == 4 and tuple(
        line.split("`", 2)[1] for line in red if line.startswith("- `")
    ) != (
        RED_CASES[4][1],
        *required_red_tests,
    ):
        violations.append("component 4 has invalid Red behavioral tests")

    red_commands = (
        list_tokens(blocks["RED command"])
        if blocks["RED command"] is not None
        else None
    )
    if blocks["RED command"] is not None and red_commands != [
        RED_COMMANDS.get(component_id)
    ]:
        violations.append(f"component {component_id} missing executable RED command")
    red_evidence = (
        list_tokens(blocks["RED evidence"])
        if blocks["RED evidence"] is not None
        else None
    )
    if blocks["RED evidence"] is not None and red_evidence != [
        RED_EVIDENCE.get(component_id)
    ]:
        violations.append(f"component {component_id} missing RED evidence")

    frameworks = (
        list_tokens(blocks["Frameworks"]) if blocks["Frameworks"] is not None else None
    )
    if blocks["Frameworks"] is not None and (
        frameworks is None
        or set(frameworks) != TARGET_FRAMEWORKS
        or len(frameworks) != 2
    ):
        violations.append(f"component {component_id} has invalid Frameworks")

    lanes = (
        list_tokens(blocks["tmux lanes"]) if blocks["tmux lanes"] is not None else None
    )
    if blocks["tmux lanes"] is not None and (
        lanes is None
        or not (
            (set(lanes) == TMUX_LANES and len(lanes) == len(TMUX_LANES))
            or lanes == ["not applicable"]
        )
    ):
        violations.append(f"component {component_id} has invalid tmux lanes")

    updates = "\n".join(nonblank(blocks["Ledger updates"] or []))
    if blocks["Ledger updates"] is not None and (
        any(not line.startswith("- ") for line in nonblank(blocks["Ledger updates"]))
        or not {
            "implementationStatus=implemented",
            "evidenceStatus=verified",
        }
        <= set(re.findall(r"(?:implementationStatus|evidenceStatus)=[a-z_]+", updates))
        or "before the phase-aware validator runs" not in updates
    ):
        violations.append(f"component {component_id} has invalid Ledger updates")

    commit = nonblank(blocks["Atomic commit"] or [])
    subject_lines = [line for line in commit if re.fullmatch(r"`[^`\n]+`", line)]
    subject = subject_lines[0][1:-1] if len(subject_lines) == 1 else ""
    subject_pattern = re.compile(
        r"^[A-Za-z][A-Za-z0-9._/-]*"
        r"\([a-z]+(?:\[[A-Za-z0-9._/-]+\])?\): [A-Za-z0-9].+$"
    )
    if blocks["Atomic commit"] is not None and (
        not commit
        or len(subject_lines) != 1
        or commit[0] != subject_lines[0]
        or len(subject) > 50
        or subject_pattern.fullmatch(subject) is None
    ):
        violations.append(f"component {component_id} has invalid Atomic commit subject")
    why_lines = [line for line in commit if line.startswith("why:")]
    if blocks["Atomic commit"] is not None and (
        len(why_lines) != 1 or not why_lines[0].removeprefix("why:").strip()
    ):
        violations.append(f"component {component_id} has invalid Atomic commit why")
    what_indexes = [index for index, line in enumerate(commit) if line == "what:"]
    if blocks["Atomic commit"] is not None and (
        len(what_indexes) != 1
        or not any(
            line.startswith("- ") and line.removeprefix("- ").strip()
            for line in commit[what_indexes[0] + 1 :]
        )
    ):
        violations.append(f"component {component_id} has invalid Atomic commit what")
    if blocks["Atomic commit"] is not None and any(
        len(line) > 72 for line in commit[1:]
    ):
        violations.append(f"component {component_id} has overlong Atomic commit body")
    expected_commit_command = planned_commit_command(blocks["Atomic commit"] or [])

    gate_tokens = (
        list_tokens(blocks["Full gate"]) if blocks["Full gate"] is not None else None
    )
    gate = "\n".join(gate_tokens or []).lower()
    gate_requirements = ["net8.0", "net10.0", "dotnet format", "dotnet build"]
    if lanes == sorted(TMUX_LANES) or (lanes is not None and set(lanes) == TMUX_LANES):
        gate_requirements.append("run-matrix.sh")
    if blocks["Full gate"] is not None and (
        gate_tokens is None
        or any(requirement not in gate for requirement in gate_requirements)
    ):
        violations.append(f"component {component_id} has invalid Full gate")
    if gate_tokens is not None and any(
        "dotnet test " in command and "--project " not in command
        for command in gate_tokens
    ):
        violations.append(
            f"component {component_id} has positional dotnet test project"
        )
    if gate_tokens is not None and any(
        "--no-build" in command and "--configuration Release" not in command
        for command in gate_tokens
    ):
        violations.append(
            f"component {component_id} has non-Release --no-build command"
        )
    if gate_tokens is not None and any(
        "dotnet build " in command and "--configuration Release" not in command
        for command in gate_tokens
    ):
        violations.append(
            f"component {component_id} has non-Release dotnet build command"
        )
    if gate_tokens is not None and any(
        validator in command
        for command in gate_tokens
        for validator in ("verify_public_api.py", "verify_ledger.py")
    ):
        violations.append(
            f"component {component_id} bypasses phase-aware approval validation"
        )
    phase_command = (
        "uv run python eng/parity/verify_production_plan.py "
        f"--phase component --component {component_id} "
        "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
    )
    stage_command = (
        "uv run python eng/parity/verify_production_plan.py "
        f"--phase component --component {component_id} --print-stage-paths "
        "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md "
        "| xargs git add --"
    )
    verify_stage_command = (
        "uv run python eng/parity/verify_production_plan.py "
        f"--phase component --component {component_id} --verify-staged-scope "
        "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
    )
    clean_index_command = 'test -z "$(git diff --cached --name-only)"'
    if gate_tokens is not None and phase_command not in gate_tokens:
        violations.append(f"component {component_id} missing phase-aware Full gate")
    required_gate_commands = REQUIRED_GATE_COMMANDS.get(component_id, ())
    # A command the source commit and the evidence closure both need must appear
    # once for each, so demand is counted rather than tested for membership.
    demanded_gate_commands = collections.Counter(required_gate_commands)
    demanded_gate_commands.update(
        command
        for command in (
            phase_command,
            stage_command,
            verify_stage_command,
            "git diff --check",
            "git diff --cached --name-only",
            "git diff --cached --check",
            expected_commit_command,
            clean_index_command,
        )
        if command is not None
    )
    if gate_tokens is not None:
        present_gate_commands = collections.Counter(gate_tokens)
        if any(
            present_gate_commands[command] < demand
            for command, demand in demanded_gate_commands.items()
        ):
            violations.append(
                f"component {component_id} missing required Full gate commands"
            )
        elif not covers_in_order(gate_tokens, required_gate_commands):
            violations.append(
                f"component {component_id} has invalid required Full gate ordering"
            )
    if gate_tokens is not None and component_id == 18:
        aot_indexes = [
            gate_tokens.index(command) if command in gate_tokens else -1
            for command in AOT_COMMANDS
        ]
        if -1 not in aot_indexes and aot_indexes != sorted(aot_indexes):
            violations.append("component 18 has invalid AOT restore ordering")
        lock_generation_is_exact = all(
            gate_tokens.count(unlocked) == 1
            and gate_tokens.count(locked) == 1
            and gate_tokens.index(locked) == gate_tokens.index(unlocked) + 1
            for unlocked, locked in C18_RESTORE_PAIRS
        )
        if not lock_generation_is_exact:
            violations.append("component 18 has invalid NuGet lock generation")
    if (
        gate_tokens is not None
        and component_id in RED_BOOTSTRAP
        and any(
            "dotnet restore LibTmux.slnx" in command and "--locked-mode" not in command
            for command in gate_tokens
        )
    ):
        violations.append(
            f"component {component_id} regenerates locks during Full gate"
        )
    if gate_tokens is not None:
        phase_indexes = [
            index
            for index, command in enumerate(gate_tokens)
            if command == phase_command
        ]
        stage_indexes = [
            index
            for index, command in enumerate(gate_tokens)
            if command == stage_command
        ]
        commit_indexes = [
            index
            for index, command in enumerate(gate_tokens)
            if expected_commit_command is not None
            and command == expected_commit_command
        ]
        clean_indexes = [
            index
            for index, command in enumerate(gate_tokens)
            if command == clean_index_command
        ]
        source_commit_index = commit_indexes[0] if len(commit_indexes) == 1 else -1
        scope_indexes = [
            index
            for index, command in enumerate(gate_tokens)
            if index < source_commit_index
            and command
            in {
                verify_stage_command,
                "git diff --cached --name-only",
                "git diff --cached --check",
            }
        ]
        if (
            len(stage_indexes) != 1
            or len(scope_indexes) != 3
            or stage_indexes[0] > min(scope_indexes)
        ):
            violations.append(
                f"component {component_id} stages after cached scope inspection"
            )
        if expected_commit_command is None or len(commit_indexes) != 1:
            violations.append(
                f"component {component_id} missing exact Atomic commit command"
            )
        if len(clean_indexes) != 1:
            violations.append(
                f"component {component_id} missing clean-index checkpoint"
            )
        expected_source_sequence = (
            phase_command,
            "git diff --check",
            stage_command,
            verify_stage_command,
            "git diff --cached --name-only",
            "git diff --cached --check",
            expected_commit_command,
            clean_index_command,
        )
        source_sequence_is_exact = (
            len(phase_indexes) == 1
            and len(stage_indexes) == 1
            and len(scope_indexes) == 3
            and len(commit_indexes) == 1
            and len(clean_indexes) == 1
            and tuple(gate_tokens[phase_indexes[0] : clean_indexes[0] + 1])
            == expected_source_sequence
        )
        if not source_sequence_is_exact:
            violations.append(
                f"component {component_id} has invalid commit checkpoint order"
            )
        if (
            len(phase_indexes) == 1
            and len(stage_indexes) == 1
            and any(
                command not in {"git diff --check"}
                for command in gate_tokens[phase_indexes[0] + 1 : stage_indexes[0]]
            )
        ):
            violations.append(
                f"component {component_id} validates phase before behavioral gates"
            )
        if component_id in EVIDENCE_CLOSURE_TAILS:
            # The evidence commit must observe a clean worktree left by the
            # source commit, so both roots are checked as one exact sequence.
            expected_closure = (
                *expected_source_sequence,
                *EVIDENCE_CLOSURE_TAILS[component_id],
            )
            if (
                len(phase_indexes) != 1
                or tuple(gate_tokens[phase_indexes[0] :]) != expected_closure
            ):
                violations.append(
                    f"component {component_id} has invalid source/evidence "
                    "closure ordering"
                )
                if component_id == 18:
                    violations.append(
                        "component 18 has invalid final evidence ordering"
                    )
        elif len(clean_indexes) == 1 and gate_tokens[clean_indexes[0] + 1 :]:
            violations.append(
                f"component {component_id} validates phase before behavioral gates"
            )
        if component_id == 18 and not {
            PRECOMMIT_SOURCE_BINDING_COMMAND,
            POSTCOMMIT_SOURCE_BINDING_COMMAND,
            EVIDENCE_STAGE_COMMAND,
            EVIDENCE_SCOPE_COMMAND,
            EVIDENCE_COMMIT_COMMAND,
        }.issubset(gate_tokens):
            violations.append("component 18 has incomplete source-binding closure")


def approval_ledger(ledger: dict[str, t.Any]) -> dict[str, t.Any]:
    """Return an approval-validator copy with production claims removed.

    Parameters
    ----------
    ledger : dict[str, typing.Any]
        Current parity ledger.

    Returns
    -------
    dict[str, typing.Any]
        Deep-copied approval snapshot.

    Examples
    --------
    >>> source = {"rows": [{"implementationStatus": "implemented"}]}
    >>> approval_ledger(source)["rows"][0]["implementationStatus"]
    'not_started'
    >>> source["rows"][0]["implementationStatus"]
    'implemented'
    """
    normalized = copy.deepcopy(ledger)
    for row in t.cast(list[dict[str, t.Any]], normalized.get("rows", [])):
        row["implementationStatus"] = "not_started"
        row["evidenceStatus"] = "none"
    return normalized


def validate_phase(
    ledger: dict[str, t.Any],
    phase: str,
    component: int | None,
) -> list[str]:
    """Validate the ledger state allowed at one production phase.

    Parameters
    ----------
    ledger : dict[str, typing.Any]
        Current parity ledger.
    phase : str
        Approval, component, or closure phase.
    component : int | None
        Exact completed component for a component phase.

    Returns
    -------
    list[str]
        Stable phase violations.

    Examples
    --------
    >>> validate_phase({"rows": []}, "approval", None)
    []
    >>> validate_phase({"rows": []}, "unknown", None)
    ['invalid validation phase']
    """
    rows = t.cast(list[dict[str, t.Any]], ledger.get("rows", []))
    initial = ("not_started", "none")
    complete = ("implemented", "verified")
    statuses = [
        (row.get("implementationStatus"), row.get("evidenceStatus")) for row in rows
    ]
    if phase == "approval":
        return (
            ["approval phase has production status claims"]
            if any(status != initial for status in statuses)
            else []
        )
    if phase == "component":
        if component not in COMPONENT_IDS:
            return ["component phase requires a valid component"]
        mismatch = any(
            status
            != (
                complete
                if isinstance(row.get("componentId"), int)
                and t.cast(int, row["componentId"]) <= component
                else initial
            )
            for row, status in zip(rows, statuses, strict=True)
        )
        return ["component phase status mismatch"] if mismatch else []
    if phase == "closure":
        return (
            ["closure phase has incomplete statuses"]
            if any(status != complete for status in statuses)
            else []
        )
    return ["invalid validation phase"]


@functools.lru_cache(maxsize=1)
def declared_test_methods() -> frozenset[str]:
    """Return every test name the repository defines, in one comparable form.

    A declared Red behavioral test that nothing defines is a plan promising a
    proof nobody wrote, and reading the plan alone cannot notice that. Some
    proofs are C# methods and some are Python functions, so both are read and
    both are lowered with their underscores dropped: `Rejects_stale_trx` and
    `test_rejects_stale_trx` are the same proof written twice.
    """
    names: set[str] = set()
    for path in (CSHARP_ROOT / "tests").rglob("*.cs"):
        if "/obj/" in path.as_posix() or "/bin/" in path.as_posix():
            continue
        names.update(
            re.findall(
                r"\b(?:public|internal)\s+(?:async\s+)?[\w<>,?\[\]. ]+?\s+(\w+)\s*\(",
                path.read_text(encoding="utf-8"),
            )
        )

    for path in (CSHARP_ROOT / "eng").rglob("test_*.py"):
        names.update(
            re.findall(
                r"^def (test_\w+)", path.read_text(encoding="utf-8"), re.MULTILINE
            )
        )

    return frozenset(comparable_test_name(name) for name in names)


def comparable_test_name(name: str) -> str:
    """Return one test name in the form both languages compare equal in."""
    return name.removeprefix("test_").replace("_", "").casefold()


def defines_test(test_name: str) -> bool:
    """Return whether the test tree defines one declared Red behavioral test."""
    method = test_name.rsplit(".", 1)[-1]
    return comparable_test_name(method) in declared_test_methods()


def stage_paths(markdown: str, component: int) -> list[str]:
    r"""Return one component's exact owned and shared staging allow-list.

    Parameters
    ----------
    markdown : str
        Production plan Markdown.
    component : int
        Component ID.

    Returns
    -------
    list[str]
        Sorted exact repository paths.

    Examples
    --------
    >>> stage_paths("## Component 1: One\n### Files\n- `a`\n"
    ...             "### Shared files\n- `b`\n", 1)
    ['a', 'b']
    """
    components, _ = parse_markdown(markdown)
    matches = [entry for entry in components if entry["id"] == component]
    if len(matches) != 1:
        raise ValueError(component, "must appear exactly once")
    fields = t.cast(dict[str, list[list[str]]], matches[0]["fields"])
    paths: set[str] = set()
    for field_name in ("Files", "Shared files"):
        blocks = fields.get(field_name, [])
        if len(blocks) != 1:
            raise ValueError(component, "has invalid field", field_name)
        tokens = list_tokens(blocks[0])
        if tokens is None or any(
            EXACT_PATH_RE.fullmatch(path) is None for path in tokens
        ):
            raise ValueError(component, "has invalid field", field_name)
        paths.update(tokens)
    return sorted(paths)


def covers_in_order(
    commands: t.Sequence[str],
    required: t.Sequence[str],
) -> bool:
    """Report whether required commands appear in order as a subsequence.

    Repeated commands match distinct positions, so a command demanded by both
    the source commit and the evidence closure cannot be satisfied twice by one
    line.

    Parameters
    ----------
    commands : Sequence[str]
        Full gate commands in declared order.
    required : Sequence[str]
        Commands that must appear in the given relative order.

    Returns
    -------
    bool
        True when every required command matches a later position.

    Examples
    --------
    >>> covers_in_order(["a", "b", "c"], ["a", "c"])
    True
    >>> covers_in_order(["a", "b", "c"], ["c", "a"])
    False
    >>> covers_in_order(["a", "b"], ["a", "a"])
    False
    """
    position = 0
    for command in required:
        while position < len(commands) and commands[position] != command:
            position += 1
        if position == len(commands):
            return False
        position += 1
    return True


def compare_staged_scope(
    allowed_paths: t.Iterable[str],
    staged_paths: t.Iterable[str],
) -> list[str]:
    """Compare staged files with exact declared file or directory allow-roots.

    Parameters
    ----------
    allowed_paths : Iterable[str]
        Declared Files and Shared files.
    staged_paths : Iterable[str]
        Exact paths reported by Git.

    Returns
    -------
    list[str]
        One stable violation when coverage differs.

    Examples
    --------
    >>> compare_staged_scope(["a.cs"], ["a.cs"])
    []
    >>> compare_staged_scope(["a.cs"], ["other.cs"])
    ['staged paths do not exactly match component allow-list']
    """
    allowed = set(allowed_paths)
    staged = set(staged_paths)

    def covers(root: str, path: str) -> bool:
        return path == root or path.startswith(f"{root}/")

    every_staged_path_is_allowed = all(
        any(covers(root, path) for root in allowed) for path in staged
    )
    every_allow_root_is_staged = all(
        any(covers(root, path) for path in staged) for root in allowed
    )
    return (
        []
        if allowed
        and staged
        and every_staged_path_is_allowed
        and every_allow_root_is_staged
        else ["staged paths do not exactly match component allow-list"]
    )


def read_staged_paths(repository: pathlib.Path = CSHARP_ROOT.parent) -> list[str]:
    """Read exact staged paths from Git without changing repository state.

    Parameters
    ----------
    repository : pathlib.Path
        Repository worktree root.

    Returns
    -------
    list[str]
        Sorted staged paths relative to the worktree.

    Raises
    ------
    RuntimeError
        Git cannot inspect the index.
    """
    try:
        output = subprocess.run(
            [
                "git",
                "-C",
                str(repository),
                "diff",
                "--cached",
                "--name-only",
                "--no-renames",
                "-z",
                "--",
            ],
            check=True,
            capture_output=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as exception:
        raise StagedScopeError(STAGED_PATH_ERROR) from exception
    return sorted(
        raw.decode("utf-8", errors="surrogateescape")
        for raw in output.split(b"\0")
        if raw
    )


def validate_public_api_files(
    markdown: str,
    public_api: dict[str, t.Any],
) -> list[str]:
    """Cross-check frozen public contracts with exact production-file owners.

    Parameters
    ----------
    markdown : str
        Production plan Markdown.
    public_api : dict[str, typing.Any]
        Frozen public API document.

    Returns
    -------
    list[str]
        Stable binding violations.

    Examples
    --------
    >>> validate_public_api_files("# Plan", {"types": []})
    ['planned production type missing from public API', 'public API type ownership incomplete or drifted', 'public API production file missing or misowned', 'planned public member missing from public API', 'public API member production file missing or misowned']
    """
    components, _ = parse_markdown(markdown)
    file_owners: dict[str, list[int]] = collections.defaultdict(list)
    api_owners: dict[str, list[int]] = collections.defaultdict(list)
    for component in components:
        fields = t.cast(dict[str, list[list[str]]], component["fields"])
        blocks = fields.get("Files", [])
        if len(blocks) != 1:
            continue
        for path in list_tokens(blocks[0]) or []:
            file_owners[path].append(t.cast(int, component["id"]))
        api_blocks = fields.get("API owners", [])
        if len(api_blocks) == 1:
            for type_id in list_tokens(api_blocks[0]) or []:
                if type_id != "not applicable":
                    api_owners[type_id].append(t.cast(int, component["id"]))
    type_ids = {
        entry.get("id")
        for entry in t.cast(list[dict[str, t.Any]], public_api.get("types", []))
        if isinstance(entry, dict) and isinstance(entry.get("id"), str)
    }
    member_ids = {
        entry.get("id")
        for entry in t.cast(list[dict[str, t.Any]], public_api.get("members", []))
        if isinstance(entry, dict) and isinstance(entry.get("id"), str)
    }
    violations: list[str] = []
    expected_api_owners = {
        type_id: component
        for component, component_types in COMPONENT_API_TYPES.items()
        for type_id in component_types
        if type_id != "not applicable"
    }
    if set(type_ids) != set(expected_api_owners):
        violations.append("planned production type missing from public API")
    if api_owners != {
        type_id: [component] for type_id, component in expected_api_owners.items()
    }:
        violations.append("public API type ownership incomplete or drifted")
    if any(
        file_owners.get(path) != [component]
        for component, path in PUBLIC_API_FILE_BINDINGS.values()
    ):
        violations.append("public API production file missing or misowned")
    if not set(PUBLIC_API_MEMBER_FILE_BINDINGS).issubset(member_ids):
        violations.append("planned public member missing from public API")
    if any(
        file_owners.get(path) != [component]
        for component, path in PUBLIC_API_MEMBER_FILE_BINDINGS.values()
    ):
        violations.append("public API member production file missing or misowned")
    return violations


def validate_format_separator_contract(ledger: dict[str, t.Any]) -> list[str]:
    """Keep the excluded delimiter bound to the approved byte framer.

    Parameters
    ----------
    ledger : dict[str, typing.Any]
        Approved parity ledger.

    Returns
    -------
    list[str]
        Stable contract violations.

    Examples
    --------
    >>> row = {
    ...     "pythonSymbolId": "libtmux.formats:FORMAT_SEPARATOR",
    ...     **FORMAT_SEPARATOR_CONTRACT,
    ... }
    >>> validate_format_separator_contract({"rows": [row]})
    []
    """
    rows = [
        row
        for row in t.cast(list[dict[str, t.Any]], ledger.get("rows", []))
        if row.get("pythonSymbolId") == "libtmux.formats:FORMAT_SEPARATOR"
    ]
    if not rows:
        return []
    if len(rows) != 1 or any(
        rows[0].get(field) != expected
        for field, expected in FORMAT_SEPARATOR_CONTRACT.items()
    ):
        return ["FORMAT_SEPARATOR exclusion contract drifted"]
    return []


def validate_c4_ledger_ownership(ledger: dict[str, t.Any]) -> list[str]:
    """Keep canonical window and pane lookup in the materialization slice.

    Parameters
    ----------
    ledger : dict[str, typing.Any]
        Approved parity ledger.

    Returns
    -------
    list[str]
        Stable ownership violation.

    Examples
    --------
    >>> validate_c4_ledger_ownership({"rows": []})
    []
    """
    rows = t.cast(list[dict[str, t.Any]], ledger.get("rows", []))
    expected = {
        "libtmux.pane:Pane.from_pane_id": {
            "componentId": 4,
            "testPath": (
                "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs"
            ),
        },
        "libtmux.window:Window.from_window_id": {
            "componentId": 4,
            "testPath": (
                "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs"
            ),
        },
    }
    lookup_rows = {
        t.cast(str, row["pythonSymbolId"]): row
        for row in rows
        if row.get("pythonSymbolId") in expected
    }
    if not lookup_rows:
        return []
    component_counts = collections.Counter(row.get("componentId") for row in rows)
    if (
        component_counts[2] != 9
        or component_counts[4] != 197
        or set(lookup_rows) != set(expected)
        or any(
            lookup_rows[row_id].get(field) != value
            for row_id, fields in expected.items()
            for field, value in fields.items()
        )
    ):
        return ["C4 lookup ledger ownership drifted"]
    return []


def validate(
    markdown: str,
    ledger: dict[str, t.Any],
    *,
    phase: str = "approval",
    component: int | None = None,
) -> list[str]:
    r"""Return structural production-plan violations.

    Parameters
    ----------
    markdown : str
        Production plan Markdown.
    ledger : dict[str, typing.Any]
        Approved parity ledger.
    phase : str
        Approval, component, or closure phase.
    component : int | None
        Exact completed component for a component phase.

    Returns
    -------
    list[str]
        Stable violation messages.

    Examples
    --------
    >>> validate("# Plan\n", {"rows": []})[:3]
    ['missing component IDs', 'component sections are out of frozen order', 'declaring type unavailable before member ownership']
    """
    components, closure = parse_markdown(markdown)
    component_counts = collections.Counter(component["id"] for component in components)
    present_components = set(component_counts)
    violations: list[str] = []
    commands = markdown_commands(markdown)
    if any(re.search(r"\bgit\s+push\b", command) for command in commands) or any(
        re.search(r"\bgit\s+tag\b", command) and command != "git tag --points-at HEAD"
        for command in commands
    ):
        violations.append("plan contains forbidden publication command")
    if any(
        "dotnet build " in command and "--configuration Release" not in command
        for command in commands
    ):
        violations.append("plan has non-Release dotnet build command")
    violations.extend(validate_format_separator_contract(ledger))
    violations.extend(validate_c4_ledger_ownership(ledger))
    if COMPONENT_IDS - present_components:
        violations.append("missing component IDs")
    if present_components - COMPONENT_IDS:
        violations.append("unknown component IDs")
    if any(count > 1 for count in component_counts.values()):
        violations.append("duplicate component IDs")
    if [component_entry["id"] for component_entry in components] != list(range(1, 19)):
        violations.append("component sections are out of frozen order")

    row_owners: dict[str, list[int]] = {}
    component_files: dict[int, set[str]] = {}
    for component_entry in components:
        validate_component(component_entry, row_owners, violations, component_files)

    file_owners: dict[str, list[int]] = collections.defaultdict(list)
    for component_id, paths in component_files.items():
        for path in paths:
            file_owners[path].append(component_id)
    if any(len(owners) > 1 for owners in file_owners.values()):
        violations.append("Files path has multiple component owners")
    if any(file_owners.get(path) != [1] for path in ENTITY_SHELL_FILES):
        violations.append("declaring type unavailable before member ownership")
    if set(file_owners) & FORBIDDEN_PRODUCTION_FILES:
        violations.append("plan contains stale or unapproved production Files")

    ledger_rows = t.cast(list[dict[str, t.Any]], ledger.get("rows", []))
    ledger_ids = {
        t.cast(str, row["pythonSymbolId"])
        for row in ledger_rows
        if isinstance(row.get("pythonSymbolId"), str)
    }
    owned_ids = set(row_owners)
    if ledger_ids - owned_ids:
        violations.append("missing ledger row IDs")
    if owned_ids - ledger_ids:
        violations.append("unknown ledger row IDs")
    if any(len(owners) > 1 for owners in row_owners.values()):
        violations.append("duplicate ledger row IDs")

    rows_by_id = {
        t.cast(str, row["pythonSymbolId"]): row
        for row in ledger_rows
        if isinstance(row.get("pythonSymbolId"), str)
    }
    for row_id, owners in row_owners.items():
        if row_id not in rows_by_id or len(owners) != 1:
            continue
        frozen_component = rows_by_id[row_id].get("componentId")
        if frozen_component is not None and frozen_component != owners[0]:
            violations.append("ledger row assigned to wrong component")
            break

    invalid_test_path = False
    missing_owned_test_path = False
    for row in ledger_rows:
        ledger_row_id = row.get("pythonSymbolId")
        test_path = row.get("testPath")
        if (
            not isinstance(test_path, str)
            or not EXACT_PATH_RE.fullmatch(test_path)
            or any(character in test_path for character in "*?[]{}")
        ):
            invalid_test_path = True
            continue
        owners = (
            row_owners.get(ledger_row_id, []) if isinstance(ledger_row_id, str) else []
        )
        if len(owners) == 1 and test_path not in component_files.get(owners[0], set()):
            missing_owned_test_path = True
    if invalid_test_path:
        violations.append("ledger row has invalid testPath")
    if missing_owned_test_path:
        violations.append("ledger row testPath missing from owning component Files")

    if not closure:
        violations.append("missing Closure section")
    for gate_name, required_tokens in CLOSURE_GATES.items():
        blocks = closure.get(gate_name, [])
        if not blocks:
            violations.append(f"closure missing {gate_name} gate")
            continue
        if len(blocks) != 1:
            violations.append(f"closure has duplicate {gate_name} gate")
            continue
        lines = nonblank(blocks[0])
        content = "\n".join(lines).lower()
        if (
            not content
            or any(not line.startswith("- ") for line in lines)
            or any(token not in content for token in required_tokens)
        ):
            violations.append(f"closure has invalid {gate_name} gate")

        closure_commands = {
            match.group("token")
            for line in lines
            if (match := LIST_TOKEN_RE.fullmatch(line)) is not None
        }
        if not set(REQUIRED_CLOSURE_COMMANDS.get(gate_name, ())).issubset(
            closure_commands
        ):
            violations.append(f"closure missing required {gate_name} commands")
        if (
            gate_name == "Repository quality"
            and closure_commands & FORBIDDEN_ROOT_QUALITY_COMMANDS
        ):
            violations.append("closure has invalid Repository quality commands")
        if any(
            "--no-build" in command and "--configuration Release" not in command
            for command in closure_commands
        ):
            violations.append(f"closure has non-Release {gate_name} command")

    violations.extend(validate_phase(ledger, phase, component))
    return violations


def load_json(path: pathlib.Path) -> dict[str, t.Any]:
    """Load a JSON object from an exact repository path.

    Parameters
    ----------
    path : pathlib.Path
        JSON path.

    Returns
    -------
    dict[str, typing.Any]
        Parsed document.

    Examples
    --------
    >>> len(load_json(LEDGER_PATH)["rows"]) > 0
    True
    """
    with path.open(encoding="utf-8") as file_handle:
        return t.cast(dict[str, t.Any], json.load(file_handle))


def validate_approval_contracts(ledger: dict[str, t.Any]) -> list[str]:
    """Run strict approval validators against a normalized ledger copy.

    Parameters
    ----------
    ledger : dict[str, typing.Any]
        Current production ledger.

    Returns
    -------
    list[str]
        Prefixed approval-contract violations.

    Examples
    --------
    >>> isinstance(validate_approval_contracts(load_ledger()), list)
    True
    """
    normalized = approval_ledger(ledger)
    public_api = runpy.run_path(str(PUBLIC_API_VALIDATOR_PATH))
    ledger_validator = runpy.run_path(str(LEDGER_VALIDATOR_PATH))
    public_violations = t.cast(
        t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]],
        public_api["validate"],
    )(load_json(PUBLIC_API_PATH), normalized)
    public_violations.extend(
        t.cast(t.Callable[[], list[str]], public_api["validate_repository"])()
    )
    inventory = load_json(INVENTORY_PATH)
    ledger_violations = t.cast(
        t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]],
        ledger_validator["validate"],
    )(inventory, normalized)
    ledger_violations.extend(
        t.cast(
            t.Callable[[dict[str, t.Any], dict[str, t.Any]], list[str]],
            ledger_validator["validate_error_policies"],
        )(load_json(ERROR_POLICIES_PATH), inventory)
    )
    return [
        *(f"public API approval: {violation}" for violation in public_violations),
        *(f"ledger approval: {violation}" for violation in ledger_violations),
    ]


def main(argv: list[str] | None = None) -> int:
    """Validate one production plan from the command line.

    Parameters
    ----------
    argv : list[str] | None
        Optional command-line arguments.

    Returns
    -------
    int
        Zero when valid, one for violations, or two for invalid usage.

    Examples
    --------
    >>> main([])
    2
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("plan", nargs="?", type=pathlib.Path)
    parser.add_argument(
        "--phase",
        choices=("approval", "component", "closure"),
        default="approval",
    )
    parser.add_argument("--component", type=int)
    parser.add_argument("--print-stage-paths", action="store_true")
    parser.add_argument("--verify-staged-scope", action="store_true")
    parser.add_argument(
        "--verify-final-evidence-staged-scope",
        action="store_true",
    )
    parser.add_argument(
        "--verify-retained-evidence-staged-scope",
        action="store_true",
    )
    arguments = parser.parse_args(argv)
    if arguments.plan is None:
        parser.print_usage(sys.stderr)
        return 2
    if (arguments.phase == "component") != (arguments.component is not None):
        parser.print_usage(sys.stderr)
        return 2
    if (
        arguments.print_stage_paths
        or arguments.verify_staged_scope
        or arguments.verify_retained_evidence_staged_scope
    ) and arguments.phase != "component":
        parser.print_usage(sys.stderr)
        return 2
    if arguments.verify_final_evidence_staged_scope and arguments.phase != "closure":
        parser.print_usage(sys.stderr)
        return 2
    if (
        arguments.verify_retained_evidence_staged_scope
        and arguments.component not in RETAINED_EVIDENCE_SCOPES
    ):
        parser.print_usage(sys.stderr)
        return 2
    if (
        sum(
            (
                arguments.print_stage_paths,
                arguments.verify_staged_scope,
                arguments.verify_final_evidence_staged_scope,
                arguments.verify_retained_evidence_staged_scope,
            )
        )
        > 1
    ):
        parser.print_usage(sys.stderr)
        return 2
    markdown = t.cast(pathlib.Path, arguments.plan).read_text(encoding="utf-8")
    current_ledger = load_ledger()
    violations = validate(
        markdown,
        current_ledger,
        phase=arguments.phase,
        component=arguments.component,
    )
    violations.extend(validate_public_api_files(markdown, load_json(PUBLIC_API_PATH)))
    violations.extend(validate_approval_contracts(current_ledger))
    if arguments.verify_staged_scope and not violations:
        try:
            current_staged_paths = read_staged_paths()
        except RuntimeError as exception:
            violations.append(str(exception))
        else:
            violations.extend(
                compare_staged_scope(
                    stage_paths(markdown, t.cast(int, arguments.component)),
                    current_staged_paths,
                )
            )
    if arguments.verify_final_evidence_staged_scope and not violations:
        try:
            current_staged_paths = read_staged_paths()
        except RuntimeError as exception:
            violations.append(str(exception))
        else:
            violations.extend(
                compare_staged_scope(
                    [FINAL_EVIDENCE_ROOT, VERSION_DELTA_PATH],
                    current_staged_paths,
                )
            )
    if arguments.verify_retained_evidence_staged_scope and not violations:
        try:
            current_staged_paths = read_staged_paths()
        except RuntimeError as exception:
            violations.append(str(exception))
        else:
            violations.extend(
                compare_staged_scope(
                    RETAINED_EVIDENCE_SCOPES[t.cast(int, arguments.component)],
                    current_staged_paths,
                )
            )
    if violations:
        for violation in violations:
            print(violation, file=sys.stderr)
        return 1
    if arguments.print_stage_paths:
        for path in stage_paths(markdown, t.cast(int, arguments.component)):
            print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
