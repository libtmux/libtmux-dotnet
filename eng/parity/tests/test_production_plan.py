"""Contract tests for the production implementation plan validator."""

# ruff: noqa: E501

from __future__ import annotations

import copy
import os
import pathlib
import runpy
import shlex
import typing as t

import pytest

COMPONENT_IDS = tuple(range(1, 19))
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
TMUX_LANES = ("3.2a", "3.3a", "3.4", "3.5", "3.6", "3.7a", "3.7b")
CLOSURE_GATES = (
    "Package",
    "Public API",
    "Independent review",
    "Repository quality",
    "Diff integrity",
    "Staged scope",
    "Clean worktree",
    "Publication boundary",
    "Platform workflow configuration",
    "macOS tmux workflow configuration",
    "External workflow evidence",
    "Packed consumers",
    "Executable examples",
    "NativeAOT",
    "Final matrix evidence",
)
CLOSURE_DETAILS = {
    "Package": (
        "Inspect package metadata, `.nupkg`, `.snupkg`, SourceLink JSON, "
        "repository revision, exact dependencies, and privacy redaction."
    ),
    "Public API": (
        "Approve Public API analyzer output and require no parity-ledger "
        "implementation or evidence gaps."
    ),
    "Independent review": (
        "Resolve fresh Framework Design Guidelines, Python-parity, and tmux reviews."
    ),
    "Repository quality": (
        "Run root Ruff formatting and lint, mypy, pytest doctests, and the docs build."
    ),
    "Diff integrity": "Run `git diff --check`.",
    "Staged scope": (
        "Require the staged paths to match the declared component allow-list exactly."
    ),
    "Clean worktree": "Require empty `git status --porcelain` output.",
    "Publication boundary": (
        "Create local commits only: this plan never runs push or tag-creation "
        "commands; record local branch, HEAD, tag, and upstream provenance without "
        "claiming remote publication proof."
    ),
    "Platform workflow configuration": (
        "Validate Linux, macOS, and Windows build and unit workflow configuration "
        "only; this local check does not execute those runtime jobs."
    ),
    "macOS tmux workflow configuration": (
        "Validate the current-stable macOS tmux integration workflow configuration "
        "only; this local check does not execute tmux on macOS."
    ),
    "External workflow evidence": (
        "After a user-owned push, hand off collection of immutable workflow run IDs "
        "and URLs; local configuration validation is not runtime evidence."
    ),
    "Packed consumers": "Run packed consumers on `net8.0` and `net10.0`.",
    "Executable examples": "Execute every documented real-tmux example.",
    "NativeAOT": "Publish and execute trimmed NativeAOT for `net8.0` and `net10.0`.",
    "Final matrix evidence": (
        "Generate the source-bound final tmux matrix evidence bundle from the clean "
        "production commit, retain it in an evidence-only closure commit, require its evaluated "
        "commit to equal `HEAD^`, recompute the evaluated-commit tree fingerprint, "
        "and constrain the descendant diff to the final evidence root."
    ),
}
BOOTSTRAP_FILES = (
    "LibTmux.slnx",
    "src/LibTmux/LibTmux.csproj",
    "src/LibTmux/packages.lock.json",
    "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj",
    "tests/LibTmux.UnitTests/packages.lock.json",
    "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj",
    "tests/LibTmux.IntegrationTests/packages.lock.json",
)
COMPONENT_DEPENDENCIES = {
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
COMPONENT_SHARED_FILES[9] += (
    "LibTmux.slnx",
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
    str(pathlib.Path(__file__).parents[1] / "reconcile_versions.py")
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
ENTITY_FRAGMENT_FILES = {
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
FOUNDATIONAL_FILES = (
    *ENTITY_SHELL_FILES,
    *EXCEPTION_FILES[:7],
    "src/LibTmux/Transport/TmuxCommandDispatcher.cs",
    "src/LibTmux/Transport/TmuxCommandFailure.cs",
    "tests/LibTmux.IntegrationTests/Infrastructure/RawTmuxTestContext.cs",
    "tests/LibTmux.IntegrationTests/Infrastructure/ControlModeClientScope.cs",
    "tests/LibTmux.IntegrationTests/Infrastructure/PtyAttachedClientScope.cs",
    "tests/LibTmux.TestChild/LibTmux.TestChild.csproj",
    "tests/LibTmux.TestChild/packages.lock.json",
    "tests/LibTmux.TestChild/Program.cs",
)
PROJECT_WIRING = {
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
RED_BOOTSTRAP = {
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
REQUIRED_PROJECT_FILES = {
    1: ("tests/LibTmux.UnitTests/Entities/EntityShellTests.cs",),
    3: (
        "src/LibTmux/Internal/CommandFlagCatalog.cs",
        "src/LibTmux/Internal/FormatCatalog.cs",
        "src/LibTmux/Internal/FormatFieldDescriptor.cs",
    ),
    4: (
        "src/LibTmux/Materialization/SeparatedRowFramer.cs",
        "src/LibTmux/Materialization/MaterializationContext.cs",
    ),
    8: (
        "src/LibTmux.Generators/LibTmux.Generators.csproj",
        "src/LibTmux.Generators/FieldCatalogGenerator.cs",
        "src/LibTmux.Generators/packages.lock.json",
    ),
    9: (
        "src/LibTmux.Query.Json/LibTmux.Query.Json.csproj",
        "src/LibTmux.Query.Json/QueryJsonSerializerContext.cs",
        "src/LibTmux.Query.Json/QueryDocumentJsonConverter.cs",
        "src/LibTmux.Query.Json/libtmux-query-v1.schema.json",
        "src/LibTmux.Query.Json/packages.lock.json",
    ),
    10: ("src/LibTmux/Requests/AttachSessionRequest.cs",),
    11: ("src/LibTmux/Requests/DisplayMessageRequest.cs",),
    12: ("src/LibTmux/Requests/DisplayPopupRequest.cs",),
    18: (
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
    ),
}
PUBLIC_API_FILE_BINDINGS = {
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
    "T:LibTmux.Internal.MaterializationContext": (
        4,
        "src/LibTmux/Materialization/MaterializationContext.cs",
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
PUBLIC_API_MEMBER_FILE_BINDINGS = {
    "P:LibTmux.Server.Version": (
        3,
        "src/LibTmux/Server.Version.cs",
    ),
}
FORMAT_SEPARATOR_CONTRACT = {
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
FORBIDDEN_PRODUCTION_FILES = (
    "src/LibTmux/Materialization/FieldCatalog.cs",
    "src/LibTmux/Requests/AttachClientRequest.cs",
    "src/LibTmux/Requests/DisplayOverlayRequest.cs",
    "src/LibTmux/Internal/XunitTmuxHarness.cs",
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
C18_RESTORE_PAIRS = {
    "core default": CORE_RESTORE_PAIR,
    "Query.Json default": JSON_DEFAULT_RESTORE_PAIR,
    "Query.Json packed": JSON_PACKED_RESTORE_PAIR,
    "local-feed solution": LOCAL_FEED_SOLUTION_RESTORE_PAIR,
    "NativeAOT linux-x64 RID": AOT_RID_RESTORE_PAIR,
}
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
ROOT_QUALITY_COMMANDS = (
    "uv run ruff format --check .",
    "uv run ruff check .",
    "uv run mypy",
    "uv run mypy eng/parity",
    "uv run mypy eng/evidence",
    "uv run pytest --doctest-modules",
    "just build-docs",
)
PUBLICATION_PROVENANCE_COMMANDS = (
    "git branch --show-current",
    "git rev-parse HEAD",
    "git tag --points-at HEAD",
    "git status --short --branch",
)
REQUIRED_GATE_COMMANDS = {
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
REQUIRED_CLOSURE_COMMANDS = {
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
REQUIRED_RED_TESTS = {
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
RED_EVIDENCE = {
    component: f"artifacts/tdd/component-{component:02d}.trx"
    for component in COMPONENT_IDS
}
RED_TEST_NAMESPACES = {
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
RED_TEST_IDENTITIES = {
    component: f"{RED_TEST_NAMESPACES[component]}.{test_name}"
    for component, (_, test_name) in RED_CASES.items()
}
RED_COMMANDS = {
    component: (
        "uv run python eng/parity/require_red.py "
        f"--project {project} --configuration Release --framework net8.0 "
        f"--no-restore --test {RED_TEST_IDENTITIES[component]} "
        f"--evidence {RED_EVIDENCE[component]}"
    )
    for component, (project, _) in RED_CASES.items()
}


def parity_test_path(component: int) -> str:
    """Return the component's frozen parity-evidence path.

    Examples
    --------
    >>> parity_test_path(3)
    'tests/LibTmux.IntegrationTests/Parity/Component03ParityTests.cs'
    """
    return (
        "tests/LibTmux.IntegrationTests/Parity/"
        f"Component{component:02d}ParityTests.cs"
    )


def validator_path() -> pathlib.Path:
    """Return the production-plan validator path.

    Examples
    --------
    >>> validator_path().name
    'verify_production_plan.py'
    """
    return pathlib.Path(__file__).parents[1] / "verify_production_plan.py"


def validator() -> t.Callable[..., list[str]]:
    """Load the production-plan validator without importing a package."""
    namespace = runpy.run_path(str(validator_path()))
    return t.cast(t.Callable[..., list[str]], namespace["validate"])


def production_plan_path() -> pathlib.Path:
    """Return the production plan document, or skip when it is not here.

    The plan lived beside this project in the monorepo it was imported out of
    and is not part of the library, so a checkout that does not have it fails
    these tests for a reason that says nothing about the code.

    Returns
    -------
    pathlib.Path
        The plan document to parse.
    """
    configured = os.environ.get("LIBTMUX_PRODUCTION_PLAN")
    plan_path = (
        pathlib.Path(configured).expanduser()
        if configured
        else pathlib.Path(__file__).parents[3]
        / "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
    )
    if not plan_path.is_file():
        pytest.skip(
            f"{plan_path} is not here. Point LIBTMUX_PRODUCTION_PLAN at the "
            "production plan to run the checks that read it.",
        )
    return plan_path


def validator_namespace() -> dict[str, t.Any]:
    """Load every production-plan validator entry point.

    Examples
    --------
    >>> "validate" in validator_namespace()
    True
    """
    return runpy.run_path(str(validator_path()))


def ledger() -> dict[str, t.Any]:
    """Return a minimal ledger with one row per production component.

    Examples
    --------
    >>> len(ledger()["rows"])
    18
    """
    return {
        "rows": [
            {
                "pythonSymbolId": f"libtmux.component{component}:symbol",
                "componentId": component,
                "testPath": parity_test_path(component),
                "implementationStatus": "not_started",
                "evidenceStatus": "none",
            }
            for component in COMPONENT_IDS
        ]
    }


def completed_ledger(component: int) -> dict[str, t.Any]:
    """Return ledger state after one component gate.

    Examples
    --------
    >>> completed_ledger(2)["rows"][0]["implementationStatus"]
    'implemented'
    >>> completed_ledger(2)["rows"][2]["implementationStatus"]
    'not_started'
    """
    document = copy.deepcopy(ledger())
    for row in document["rows"]:
        if row["componentId"] <= component:
            row["implementationStatus"] = "implemented"
            row["evidenceStatus"] = "verified"
    return document


def public_api() -> dict[str, t.Any]:
    """Return the public types with frozen production-file bindings.

    Examples
    --------
    >>> len(public_api()["types"])
    169
    """
    return {
        "types": [
            {"id": type_id}
            for component in COMPONENT_IDS
            for type_id in COMPONENT_API_TYPES[component]
            if type_id != "not applicable"
        ],
        "members": [{"id": member_id} for member_id in PUBLIC_API_MEMBER_FILE_BINDINGS],
    }


def atomic_commit_command(component: int) -> str:
    """Return the exact one-commit checkpoint for a component.

    Examples
    --------
    >>> atomic_commit_command(1).endswith("| git commit --file -")
    True
    """
    lines = (
        f"Component{component}(feat): Implement slice",
        "",
        "why: Preserve approved behavior in one reviewable component.",
        "",
        "what:",
        "- Implement the owned production and test files.",
        "- Verify every owned parity row.",
    )
    return (
        "printf '%s\\n' "
        + " ".join(shlex.quote(line) for line in lines)
        + " | git commit --file -"
    )


def component_section(component: int) -> str:
    """Return one structurally complete component task.

    Examples
    --------
    >>> "## Component 1:" in component_section(1)
    True
    """
    row_id = f"libtmux.component{component}:symbol"
    lanes = "\n".join(f"- `{lane}`" for lane in TMUX_LANES)
    files = list(COMPONENT_FILES[component])
    file_lines = "\n".join(f"- `{path}`" for path in files)
    api_lines = "\n".join(
        f"- {'``' if '`' in type_id else '`'}{type_id}{'``' if '`' in type_id else '`'}"
        for type_id in COMPONENT_API_TYPES[component]
    )
    dependencies = "\n".join(
        f"- `{dependency}`"
        for dependency in COMPONENT_DEPENDENCIES.get(component, ("none",))
    )
    shared_lines = "\n".join(
        f"- `{path}`"
        for path in COMPONENT_SHARED_FILES.get(
            component,
            ("docs/parity/parity-ledger.json",),
        )
    )
    wiring = "\n".join(
        f"- `{entry}`" for entry in PROJECT_WIRING.get(component, ("not applicable",))
    )
    transport_contract = ""
    if component == 1:
        transport_contract = "\n\n### Transport contract\n\n" + "\n".join(
            f"- `{entry}`" for entry in COMPONENT_ONE_TRANSPORT_CONTRACT
        )
    red_runner_contract = ""
    if component == 1:
        red_runner_contract = "\n\n### RED runner contract\n\n" + "\n".join(
            f"- `{entry}`" for entry in RED_RUNNER_CONTRACT
        )
    red_evidence_freshness = ""
    if component == 1:
        red_evidence_freshness = "\n\n### RED evidence freshness\n\n" + "\n".join(
            f"- `{entry}`" for entry in RED_EVIDENCE_FRESHNESS_CONTRACT
        )
    tmux_37_transition_proof = ""
    if component == 3:
        tmux_37_transition_proof = "\n\n### tmux 3.7 transition proof\n\n" + "\n".join(
            f"- `{entry}`" for entry in TMUX_37_TRANSITION_PROOF_CONTRACT
        )
    version_policy_proofs = ""
    if component in VERSION_POLICY_PROOFS_BY_COMPONENT:
        version_policy_proofs = "\n\n### Version policy proofs\n\n" + "\n".join(
            f"- `{entry}`" for entry in VERSION_POLICY_PROOFS_BY_COMPONENT[component]
        )
    materialization_contract = ""
    if component == 4:
        materialization_contract = "\n\n### Materialization contract\n\n" + "\n".join(
            f"- `{entry}`" for entry in C4_MATERIALIZATION_CONTRACT
        )
    failure_corpus_contract = ""
    if component == 1:
        failure_corpus_contract = "\n\n### Failure corpus contract\n\n" + "\n".join(
            f"- `{entry}`" for entry in C1_FAILURE_CORPUS_CONTRACT
        )
    red_bootstrap = ""
    if component in RED_BOOTSTRAP:
        red_bootstrap = "\n\n### RED bootstrap\n\n" + "\n".join(
            f"- `{entry}`" for entry in RED_BOOTSTRAP[component]
        )
    red_tests = list(
        dict.fromkeys((RED_CASES[component][1], *REQUIRED_RED_TESTS.get(component, ())))
    )
    red_lines = "\n".join(
        f"- `{test}` must fail before production code is added." for test in red_tests
    )
    red_command = RED_COMMANDS[component]
    red_evidence = RED_EVIDENCE[component]
    unit_project = "tests/LibTmux.UnitTests/LibTmux.UnitTests.csproj"
    integration_project = (
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj"
    )
    common_commands = [
        "dotnet format --verify-no-changes --no-restore",
        PUBLIC_API_BUILD_COMMAND,
        (
            f"dotnet test --project {unit_project} "
            "--configuration Release --framework net8.0 --no-build"
        ),
        (
            f"dotnet test --project {unit_project} "
            "--configuration Release --framework net10.0 --no-build"
        ),
    ]
    if component == 3:
        behavioral_commands = [
            "dotnet restore LibTmux.slnx --locked-mode",
            *common_commands,
            NON_RETAINED_MATRIX_COMMAND,
        ]
    elif component == 18:
        behavioral_commands = [
            *PACKAGE_COMMANDS,
            *common_commands,
            *PACKED_CONSUMER_COMMANDS,
            *EXAMPLE_COMMANDS,
            *AOT_COMMANDS,
            *WORKFLOW_CONFIGURATION_COMMANDS,
            NON_RETAINED_MATRIX_COMMAND,
        ]
    else:
        behavioral_commands = [
            "dotnet restore LibTmux.slnx --locked-mode",
            *common_commands,
            f"eng/tmux/run-matrix.sh {integration_project}",
        ]
    phase_command = (
        "uv run python eng/parity/verify_production_plan.py "
        f"--phase component --component {component} "
        "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
    )
    stage_command = (
        "uv run python eng/parity/verify_production_plan.py "
        f"--phase component --component {component} --print-stage-paths "
        "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md "
        "| xargs git add --"
    )
    verify_stage_command = (
        "uv run python eng/parity/verify_production_plan.py "
        f"--phase component --component {component} --verify-staged-scope "
        "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
    )
    gate_commands = [
        *behavioral_commands,
        phase_command,
        "git diff --check",
        stage_command,
        verify_stage_command,
        "git diff --cached --name-only",
        "git diff --cached --check",
        atomic_commit_command(component),
        'test -z "$(git diff --cached --name-only)"',
    ]
    if component == 3:
        gate_commands.extend((SOURCE_WORKTREE_CLEAN_COMMAND, *C3_EVIDENCE_COMMANDS))
    if component == 18:
        gate_commands.extend((SOURCE_WORKTREE_CLEAN_COMMAND, *FINAL_MATRIX_COMMANDS))
    gate_lines = "\n".join(f"- `{command}`" for command in gate_commands)
    return f"""## Component {component}: Production slice {component}

### Files

{file_lines}

### API owners

{api_lines}

### Shared files

{shared_lines}

### Depends on

{dependencies}

### Project wiring

{wiring}
{transport_contract}
{red_runner_contract}
{red_evidence_freshness}
{tmux_37_transition_proof}
{version_policy_proofs}
{materialization_contract}
{failure_corpus_contract}
{red_bootstrap}

### Ledger rows

- `{row_id}`

### Red behavioral test

{red_lines}

### RED command

- `{red_command}`

### RED evidence

- `{red_evidence}`

### Frameworks

- `net8.0`
- `net10.0`

### tmux lanes

{lanes}

### Ledger updates

- Set `implementationStatus=implemented` for every owned row.
- Set `evidenceStatus=verified` after behavioral commands pass and before the phase-aware validator runs.

### Atomic commit

`Component{component}(feat): Implement slice`

why: Preserve approved behavior in one reviewable component.

what:
- Implement the owned production and test files.
- Verify every owned parity row.

### Full gate

{gate_lines}
"""


def complete_plan() -> str:
    """Return a minimal structurally valid 18-component plan.

    Examples
    --------
    >>> complete_plan().count("## Component ")
    18
    """
    closure = "\n\n".join(
        "\n".join(
            (
                f"### {gate} gate\n",
                f"- {CLOSURE_DETAILS[gate]}",
                *(
                    f"- `{command}`"
                    for command in REQUIRED_CLOSURE_COMMANDS.get(gate, ())
                ),
            )
        )
        for gate in CLOSURE_GATES
    )
    components = "\n".join(component_section(component) for component in COMPONENT_IDS)
    return f"""# LibTmux C# production implementation

{components}
## Closure

{closure}
"""


def test_minimal_plan_reports_missing_components_and_rows() -> None:
    """Reject a plan that does not own the complete implementation."""
    violations = validator()(
        "# LibTmux C# production implementation\n",
        {"rows": [{"pythonSymbolId": "libtmux:Server"}]},
    )
    assert "missing component IDs" in violations
    assert "missing ledger row IDs" in violations


def test_complete_plan_passes_structural_validation() -> None:
    """Accept complete component ownership and closure gates."""
    assert validator()(complete_plan(), ledger()) == []


@pytest.mark.parametrize("component", [1, 9, 18])
def test_component_ids_must_appear_exactly_once(component: int) -> None:
    """Reject missing and duplicate component task ownership."""
    section = component_section(component)
    missing = complete_plan().replace(section, "", 1)
    duplicate = complete_plan().replace(section, section + section, 1)

    assert "missing component IDs" in validator()(missing, ledger())
    assert "duplicate component IDs" in validator()(duplicate, ledger())


def test_unknown_component_ids_are_rejected() -> None:
    """Reject tasks outside the approved 18-component design."""
    unknown = component_section(18).replace(
        "## Component 18:",
        "## Component 19:",
        1,
    )
    plan = complete_plan().replace("## Closure", unknown + "## Closure", 1)
    assert "unknown component IDs" in validator()(plan, ledger())


def test_ledger_rows_must_be_owned_exactly_once() -> None:
    """Reject missing, duplicate, and unknown ledger-row ownership."""
    plan = complete_plan()
    row = "- `libtmux.component8:symbol`\n"
    missing = plan.replace(row, "", 1)
    duplicate = plan.replace(row, row + row, 1)
    unknown = plan.replace(row, row + "- `libtmux:unknown`\n", 1)

    assert "missing ledger row IDs" in validator()(missing, ledger())
    assert "duplicate ledger row IDs" in validator()(duplicate, ledger())
    assert "unknown ledger row IDs" in validator()(unknown, ledger())


def test_ledger_rows_must_match_their_frozen_component() -> None:
    """Reject ownership that conflicts with the ledger component ID."""
    plan = (
        complete_plan()
        .replace(
            "- `libtmux.component1:symbol`",
            "- `temporary:row`",
            1,
        )
        .replace(
            "- `libtmux.component2:symbol`",
            "- `libtmux.component1:symbol`",
            1,
        )
        .replace(
            "- `temporary:row`",
            "- `libtmux.component2:symbol`",
            1,
        )
    )
    assert "ledger row assigned to wrong component" in validator()(plan, ledger())


@pytest.mark.parametrize(("field", "expected"), FORMAT_SEPARATOR_CONTRACT.items())
def test_format_separator_keeps_exact_exclusion_contract(
    field: str,
    expected: t.Any,
) -> None:
    """Keep the delimiter tombstone bound to ADR 0001 byte framing."""
    validate_contract = t.cast(
        t.Callable[[dict[str, t.Any]], list[str]],
        validator_namespace()["validate_format_separator_contract"],
    )
    row = {
        "pythonSymbolId": "libtmux.formats:FORMAT_SEPARATOR",
        **FORMAT_SEPARATOR_CONTRACT,
    }
    assert validate_contract({"rows": [row]}) == []

    invalid = copy.deepcopy(row)
    invalid[field] = "drifted" if expected is not None else "must-be-null"
    assert validate_contract({"rows": [invalid]}) == [
        "FORMAT_SEPARATOR exclusion contract drifted"
    ]


@pytest.mark.parametrize(
    "heading",
    [
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
    ],
)
def test_every_component_requires_each_structural_field(heading: str) -> None:
    """Reject tasks that omit a required implementation field."""
    plan = complete_plan().replace(f"### {heading}\n", f"### Missing {heading}\n", 1)
    assert f"component 1 missing {heading}" in validator()(plan, ledger())


def test_files_must_be_exact_repository_paths() -> None:
    """Reject vague directories and wildcard task ownership."""
    plan = complete_plan().replace(
        "- `src/LibTmux/Transport/TmuxCommandRequest.cs`",
        "- `src/LibTmux/**`",
        1,
    )
    assert "component 1 has non-exact Files" in validator()(plan, ledger())


@pytest.mark.parametrize(
    ("valid_line", "invalid_line", "message"),
    [
        (
            "- `src/LibTmux/Transport/TmuxCommandRequest.cs`",
            "src/LibTmux/Transport/TmuxCommandRequest.cs",
            "component 1 has non-exact Files",
        ),
        (
            "- `libtmux.component1:symbol`",
            "libtmux.component1:symbol",
            "component 1 has invalid Ledger rows",
        ),
        ("- `net8.0`", "net8.0", "component 1 has invalid Frameworks"),
        ("- `3.2a`", "3.2a", "component 1 has invalid tmux lanes"),
        (
            "- `dotnet format --verify-no-changes --no-restore`",
            "dotnet format --verify-no-changes --no-restore",
            "component 1 has invalid Full gate",
        ),
    ],
)
def test_structured_fields_reject_unlisted_prose(
    valid_line: str,
    invalid_line: str,
    message: str,
) -> None:
    """Reject field content outside the required Markdown list shape."""
    plan = complete_plan().replace(valid_line, invalid_line, 1)
    assert message in validator()(plan, ledger())


def test_frameworks_require_both_supported_targets_only() -> None:
    """Require the complete net8.0 and net10.0 target pair."""
    plan = complete_plan().replace("- `net10.0`\n", "", 1)
    assert "component 1 has invalid Frameworks" in validator()(plan, ledger())


def test_tmux_lanes_must_be_explicit() -> None:
    """Reject vague tmux matrix declarations."""
    plan = complete_plan().replace("- `3.2a`\n", "- `supported versions`\n", 1)
    assert "component 1 has invalid tmux lanes" in validator()(plan, ledger())


def test_ledger_updates_require_implementation_and_evidence_states() -> None:
    """Require both production ledger transitions in every component."""
    plan = complete_plan().replace(
        "- Set `evidenceStatus=verified` after behavioral commands pass and before the phase-aware validator runs.\n",
        "",
        1,
    )
    assert "component 1 has invalid Ledger updates" in validator()(plan, ledger())


def test_ledger_updates_must_precede_the_phase_validator() -> None:
    """Reject a status transition that makes the component gate circular."""
    plan = complete_plan().replace(
        "before the phase-aware validator runs",
        "after the full gate passes",
        1,
    )
    assert "component 1 has invalid Ledger updates" in validator()(plan, ledger())


def test_atomic_commit_is_exactly_one_subject() -> None:
    """Reject multiple commits inside one atomic component task."""
    subject = "`Component1(feat): Implement slice`\n"
    plan = complete_plan().replace(subject, subject + "`Second commit`\n", 1)
    assert "component 1 has invalid Atomic commit subject" in validator()(
        plan, ledger()
    )


def test_full_gate_must_cover_both_frameworks_and_tmux() -> None:
    """Reject a component gate that omits its declared matrix."""
    command = (
        "- `eng/tmux/run-matrix.sh "
        "tests/LibTmux.IntegrationTests/LibTmux.IntegrationTests.csproj`\n"
    )
    plan = complete_plan().replace(command, "", 1)
    assert "component 1 has invalid Full gate" in validator()(plan, ledger())


@pytest.mark.parametrize("path", BOOTSTRAP_FILES)
def test_component_one_must_bootstrap_the_build_graph(path: str) -> None:
    """Reject a first slice that uses projects before creating them."""
    plan = complete_plan().replace(f"- `{path}`\n", "", 1)
    assert "component 1 missing build bootstrap Files" in validator()(plan, ledger())


def test_component_one_owns_the_transport_limits_seam() -> None:
    """Reject a transport slice without its bounded-resource seam."""
    path = "src/LibTmux/Transport/TmuxTransportLimits.cs"
    plan = complete_plan().replace(f"- `{path}`\n", "", 1)
    assert "component 1 has invalid Files inventory" in validator()(plan, ledger())


@pytest.mark.parametrize("contract", COMPONENT_ONE_TRANSPORT_CONTRACT)
def test_component_one_transport_contract_is_frozen(contract: str) -> None:
    """Reject ambiguity in the approved C1 command and transport semantics."""
    plan = complete_plan().replace(f"- `{contract}`\n", "", 1)
    assert "component 1 missing frozen transport contract" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    ("component", "entry"),
    [
        (component, entry)
        for component, entries in RED_BOOTSTRAP.items()
        for entry in entries
    ],
)
def test_changed_graphs_are_generated_and_locked_before_red(
    component: int,
    entry: str,
) -> None:
    """Reject missing stubs or restore steps before C1, C8, and C9 RED."""
    section = component_section(component)
    invalid = section.replace(f"- `{entry}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} has invalid RED bootstrap" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("component", (1, 8, 9))
def test_pre_red_restore_pair_is_immediate(component: int) -> None:
    """Reject work inserted between unlocked generation and locked consumption."""
    section = component_section(component)
    locked = f"- `{SOLUTION_RESTORE_PAIR[1]}`\n"
    invalid = section.replace(locked, "- `dotnet --info`\n" + locked, 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} has invalid RED bootstrap" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("component", (1, 8, 9))
def test_full_gate_consumes_locks_without_regeneration(component: int) -> None:
    """Reject unlocked restore after the retained RED checkpoint."""
    section = component_section(component)
    prefix, full_gate = section.split("### Full gate\n\n", 1)
    full_unlocked = "dotnet restore LibTmux.slnx"
    full_locked = f"{full_unlocked} --locked-mode"
    locked = f"- `{full_locked}`\n"
    invalid_gate = full_gate.replace(
        locked,
        f"- `{full_unlocked}`\n" + locked,
        1,
    )
    invalid = prefix + "### Full gate\n\n" + invalid_gate
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} regenerates locks during Full gate" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("contract", RED_RUNNER_CONTRACT)
def test_red_runner_semantics_are_frozen(contract: str) -> None:
    """Reject a RED helper that can confuse infrastructure failure with behavior."""
    plan = complete_plan().replace(f"- `{contract}`\n", "", 1)
    assert "component 1 missing frozen RED runner contract" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("contract", RED_EVIDENCE_FRESHNESS_CONTRACT)
def test_red_runner_requires_fresh_evidence(contract: str) -> None:
    """Reject stale failed TRX reuse after build or discovery failure."""
    plan = complete_plan().replace(f"- `{contract}`\n", "", 1)
    assert "component 1 missing fresh RED evidence contract" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("contract", C1_FAILURE_CORPUS_CONTRACT)
def test_component_one_failure_corpus_is_frozen(contract: str) -> None:
    """Reject ambiguous exception, cancellation, cleanup, or byte projection behavior."""
    plan = complete_plan().replace(f"- `{contract}`\n", "", 1)
    assert "component 1 missing frozen failure corpus" in validator()(plan, ledger())


def test_component_one_red_command_executes_transport_behavior() -> None:
    """Keep the retained RED receipt bound to behavior, not structure alone."""
    structural = (
        "EntityShellTests."
        "Canonical_entities_are_public_sealed_partial_before_members_are_added"
    )
    plan = complete_plan().replace(
        RED_TEST_IDENTITIES[1],
        f"LibTmux.UnitTests.Entities.{structural}",
        1,
    )
    assert "component 1 missing executable RED command" in validator()(plan, ledger())


def test_all_mtp_commands_require_the_project_option() -> None:
    """Reject positional projects under Microsoft Testing Platform."""
    plan = complete_plan().replace(
        "dotnet test --project tests/LibTmux.UnitTests",
        "dotnet test tests/LibTmux.UnitTests",
        1,
    )
    assert "component 1 has positional dotnet test project" in validator()(
        plan, ledger()
    )


def test_component_dependencies_are_exact_and_acyclic() -> None:
    """Reject a slice that consumes a foundation it does not declare."""
    section = component_section(4)
    invalid = section.replace("- `component 3`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 4 has invalid Depends on" in validator()(plan, ledger())


def test_files_have_one_component_owner() -> None:
    """Reject source ownership shared by two atomic components."""
    section = component_section(2)
    duplicate = "- `src/LibTmux/Transport/TmuxCommandRequest.cs`\n"
    invalid = section.replace("### Files\n\n", f"### Files\n\n{duplicate}", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "Files path has multiple component owners" in validator()(plan, ledger())


@pytest.mark.parametrize("component", COMPONENT_IDS)
def test_shared_file_allow_lists_are_exact(component: int) -> None:
    """Reject undeclared staging paths hidden behind shared ownership."""
    section = component_section(component)
    first = COMPONENT_SHARED_FILES[component][0]
    invalid = section.replace(
        f"- `{first}`\n",
        f"- `{first}`\n- `unowned-{component}.txt`\n",
        1,
    )
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} has invalid Shared files" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("path", FOUNDATIONAL_FILES)
def test_component_one_owns_transport_exceptions_and_raw_harness(path: str) -> None:
    """Reject consumers scheduled before their concrete foundation."""
    plan = complete_plan().replace(f"- `{path}`\n", "", 1)
    assert "component 1 missing foundational Files" in validator()(plan, ledger())


@pytest.mark.parametrize(
    ("component", "entry"),
    [
        (component, entry)
        for component, entries in PROJECT_WIRING.items()
        for entry in entries
    ],
)
def test_project_wiring_is_explicit(component: int, entry: str) -> None:
    """Reject a project that is not connected to its solution or dependency."""
    section = component_section(component)
    invalid = section.replace(f"- `{entry}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} has invalid Project wiring" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    ("component", "path"),
    [
        (component, path)
        for component, paths in REQUIRED_PROJECT_FILES.items()
        for path in paths
    ],
)
def test_project_sources_locks_baselines_and_workflows_are_owned(
    component: int,
    path: str,
) -> None:
    """Reject generated, executable, or workflow projects with missing files."""
    section = component_section(component)
    invalid = section.replace(f"- `{path}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} missing required project Files" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    ("component", "command"),
    [
        (component, command)
        for component, commands in REQUIRED_GATE_COMMANDS.items()
        for command in commands
    ],
)
def test_version_and_closure_commands_are_exact(component: int, command: str) -> None:
    """Reject a gate that cannot emit or execute its promised artifact."""
    section = component_section(component)
    invalid = section.replace(f"- `{command}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} missing required Full gate commands" in validator()(
        plan, ledger()
    )


def test_stage_allow_list_precedes_cached_scope_checks() -> None:
    """Reject inspecting staged scope before staging declared paths."""
    section = component_section(1)
    stage = next(line for line in section.splitlines() if "xargs git add --" in line)
    inspect = "- `git diff --cached --name-only`"
    invalid = section.replace(stage, "stage-marker", 1)
    invalid = invalid.replace(inspect, stage, 1).replace("stage-marker", inspect, 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 1 stages after cached scope inspection" in validator()(
        plan, ledger()
    )


def test_component_gates_cannot_call_approval_validators_directly() -> None:
    """Route progressive ledger states through the phase-aware validator."""
    section = component_section(1)
    phase_command = (
        "uv run python eng/parity/verify_production_plan.py "
        "--phase component --component 1 "
        "docs/superpowers/plans/2026-08-09-libtmux-csharp-production.md"
    )
    invalid = section.replace(
        phase_command,
        "uv run python eng/parity/verify_public_api.py",
        1,
    )
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 1 bypasses phase-aware approval validation" in validator()(
        plan, ledger()
    )


def test_stage_paths_are_exactly_declared_files() -> None:
    """Derive staging from owned and explicitly shared paths only."""
    stage_paths = t.cast(
        t.Callable[[str, int], list[str]],
        validator_namespace()["stage_paths"],
    )
    paths = stage_paths(complete_plan(), 8)
    assert paths == sorted({*COMPONENT_FILES[8], *COMPONENT_SHARED_FILES[8]})


def test_ledger_test_path_must_be_listed_in_owning_component_files() -> None:
    """Reject evidence paths that the owning component never creates."""
    path = parity_test_path(8)
    plan = complete_plan().replace(f"- `{path}`\n", "", 1)
    assert "ledger row testPath missing from owning component Files" in validator()(
        plan, ledger()
    )


def test_ledger_test_path_must_be_an_exact_repository_path() -> None:
    """Reject missing or wildcard evidence destinations in ledger rows."""
    invalid_ledger = ledger()
    invalid_ledger["rows"][0]["testPath"] = "tests/**/ParityTests.cs"
    assert "ledger row has invalid testPath" in validator()(
        complete_plan(), invalid_ledger
    )


@pytest.mark.parametrize(
    ("component", "test_name"),
    [
        (component, test_name)
        for component, test_names in REQUIRED_RED_TESTS.items()
        for test_name in test_names
    ],
)
def test_required_behavior_families_need_named_red_tests(
    component: int,
    test_name: str,
) -> None:
    """Reject broad happy paths that omit one approved behavior family."""
    plan = complete_plan().replace(
        f"- `{test_name}` must fail", "- `omitted` must fail", 1
    )
    assert (
        f"component {component} missing required Red behavioral tests"
        in validator()(plan, ledger())
    )


def test_approval_phase_rejects_production_claims() -> None:
    """Keep the committed approval snapshot strictly unimplemented."""
    violations = validator()(
        complete_plan(),
        completed_ledger(1),
        phase="approval",
    )
    assert "approval phase has production status claims" in violations


def test_component_phase_accepts_only_the_completed_prefix() -> None:
    """Accept exact monotonic progress through the selected component."""
    assert (
        validator()(
            complete_plan(),
            completed_ledger(8),
            phase="component",
            component=8,
        )
        == []
    )


def test_component_phase_rejects_incomplete_and_future_rows() -> None:
    """Reject gaps and work claimed beyond the selected component."""
    incomplete = completed_ledger(8)
    incomplete["rows"][0]["evidenceStatus"] = "none"
    future = completed_ledger(9)
    assert "component phase status mismatch" in validator()(
        complete_plan(), incomplete, phase="component", component=8
    )
    assert "component phase status mismatch" in validator()(
        complete_plan(), future, phase="component", component=8
    )


def test_closure_phase_requires_every_row_verified() -> None:
    """Reject closure while any component remains incomplete."""
    assert (
        validator()(
            complete_plan(),
            completed_ledger(18),
            phase="closure",
        )
        == []
    )
    assert "closure phase has incomplete statuses" in validator()(
        complete_plan(), completed_ledger(17), phase="closure"
    )


def test_approval_validation_uses_a_normalized_copy() -> None:
    """Run frozen approval validators without erasing production progress."""
    normalize = t.cast(
        t.Callable[[dict[str, t.Any]], dict[str, t.Any]],
        validator_namespace()["approval_ledger"],
    )
    progressed = completed_ledger(4)
    normalized = normalize(progressed)
    assert {row["implementationStatus"] for row in normalized["rows"]} == {
        "not_started"
    }
    assert {row["evidenceStatus"] for row in normalized["rows"]} == {"none"}
    assert progressed["rows"][0]["implementationStatus"] == "implemented"


def test_strict_approval_contracts_accept_normalized_production_progress() -> None:
    """Preserve strict approval tools while the production ledger advances."""
    namespace = validator_namespace()
    progressed = namespace["load_ledger"]()
    progressed["rows"][0]["implementationStatus"] = "implemented"
    progressed["rows"][0]["evidenceStatus"] = "verified"

    assert namespace["validate_approval_contracts"](progressed) == []
    assert progressed["rows"][0]["implementationStatus"] == "implemented"


def test_cli_component_phase_prints_the_exact_stage_allow_list(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Exercise progressive validation through the production CLI."""
    namespace = validator_namespace()
    main = t.cast(t.Any, namespace["main"])
    plan_path = tmp_path / "production.md"
    plan_path.write_text(complete_plan(), encoding="utf-8")
    progressed = completed_ledger(8)
    monkeypatch.setitem(main.__globals__, "load_ledger", lambda: progressed)
    monkeypatch.setitem(
        main.__globals__,
        "validate_approval_contracts",
        lambda current: [] if current is progressed else ["wrong ledger"],
    )

    result = main(
        [
            "--phase",
            "component",
            "--component",
            "8",
            "--print-stage-paths",
            str(plan_path),
        ]
    )

    expected = namespace["stage_paths"](complete_plan(), 8)
    assert result == 0
    assert capsys.readouterr().out.splitlines() == expected


def test_cli_component_phase_verifies_the_staged_allow_list(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Exercise exact staged-scope comparison through the production CLI."""
    namespace = validator_namespace()
    main = t.cast(t.Any, namespace["main"])
    plan_path = tmp_path / "production.md"
    plan_path.write_text(complete_plan(), encoding="utf-8")
    progressed = completed_ledger(8)
    expected = namespace["stage_paths"](complete_plan(), 8)
    monkeypatch.setitem(main.__globals__, "load_ledger", lambda: progressed)
    monkeypatch.setitem(main.__globals__, "validate_approval_contracts", lambda _: [])
    monkeypatch.setitem(main.__globals__, "read_staged_paths", lambda: expected)

    arguments = [
        "--phase",
        "component",
        "--component",
        "8",
        "--verify-staged-scope",
        str(plan_path),
    ]
    assert main(arguments) == 0
    assert capsys.readouterr().err == ""

    monkeypatch.setitem(
        main.__globals__,
        "read_staged_paths",
        lambda: [*expected, "outside.txt"],
    )
    assert main(arguments) == 1
    assert "staged paths do not exactly match" in capsys.readouterr().err


def test_cli_closure_verifies_only_final_evidence_is_staged(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Allow exactly retained evidence plus the reconciled policy document."""
    namespace = validator_namespace()
    main = t.cast(t.Any, namespace["main"])
    plan_path = tmp_path / "production.md"
    plan_path.write_text(complete_plan(), encoding="utf-8")
    monkeypatch.setitem(main.__globals__, "load_ledger", lambda: completed_ledger(18))
    monkeypatch.setitem(main.__globals__, "validate_approval_contracts", lambda _: [])
    monkeypatch.setitem(
        main.__globals__,
        "read_staged_paths",
        lambda: [
            f"{FINAL_EVIDENCE_ROOT}/results.ndjson",
            VERSION_DELTA_PATH,
        ],
    )
    arguments = [
        "--phase",
        "closure",
        "--verify-final-evidence-staged-scope",
        str(plan_path),
    ]

    assert main(arguments) == 0
    assert capsys.readouterr().err == ""

    monkeypatch.setitem(
        main.__globals__,
        "read_staged_paths",
        lambda: [
            f"{FINAL_EVIDENCE_ROOT}/results.ndjson",
            "src/LibTmux/Server.cs",
        ],
    )
    assert main(arguments) == 1
    assert "staged paths do not exactly match" in capsys.readouterr().err


def test_cli_component_three_verifies_two_root_evidence_scope(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Allow only cohort 0001 and its reconciled version-delta metadata."""
    namespace = validator_namespace()
    main = t.cast(t.Any, namespace["main"])
    plan_path = tmp_path / "production.md"
    plan_path.write_text(complete_plan(), encoding="utf-8")
    monkeypatch.setitem(main.__globals__, "load_ledger", lambda: completed_ledger(3))
    monkeypatch.setitem(main.__globals__, "validate_approval_contracts", lambda _: [])
    monkeypatch.setitem(
        main.__globals__,
        "read_staged_paths",
        lambda: [f"{C3_EVIDENCE_ROOT}/results.ndjson", VERSION_DELTA_PATH],
    )
    arguments = [
        "--phase",
        "component",
        "--component",
        "3",
        "--verify-retained-evidence-staged-scope",
        str(plan_path),
    ]

    assert main(arguments) == 0
    assert capsys.readouterr().err == ""

    monkeypatch.setitem(
        main.__globals__,
        "read_staged_paths",
        lambda: [
            f"{C3_EVIDENCE_ROOT}/results.ndjson",
            VERSION_DELTA_PATH,
            "src/LibTmux/Server.cs",
        ],
    )
    assert main(arguments) == 1
    assert "staged paths do not exactly match" in capsys.readouterr().err


def test_cli_approval_phase_remains_strict(
    tmp_path: pathlib.Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """Keep the default approval invocation strict after adding phase support."""
    namespace = validator_namespace()
    main = t.cast(t.Any, namespace["main"])
    plan_path = tmp_path / "production.md"
    plan_path.write_text(complete_plan(), encoding="utf-8")
    monkeypatch.setitem(main.__globals__, "load_ledger", lambda: completed_ledger(1))
    monkeypatch.setitem(main.__globals__, "validate_approval_contracts", lambda _: [])

    assert main([str(plan_path)]) == 1
    assert "approval phase has production status claims" in capsys.readouterr().err


def test_atomic_commit_requires_short_subject_and_why_what_body() -> None:
    """Reject an unreviewable or malformed planned commit message."""
    section = component_section(1)
    long_subject = "X" * 51
    invalid_subject = section.replace(
        "`Component1(feat): Implement slice`",
        f"`{long_subject}`",
        1,
    )
    missing_why = section.replace(
        "why: Preserve approved behavior in one reviewable component.\n",
        "",
        1,
    )
    missing_what = section.replace(
        "what:\n- Implement the owned production and test files.\n",
        "",
        1,
    )
    plan = complete_plan()
    assert "component 1 has invalid Atomic commit subject" in validator()(
        plan.replace(section, invalid_subject, 1), ledger()
    )
    assert "component 1 has invalid Atomic commit why" in validator()(
        plan.replace(section, missing_why, 1), ledger()
    )
    assert "component 1 has invalid Atomic commit what" in validator()(
        plan.replace(section, missing_what, 1), ledger()
    )


@pytest.mark.parametrize("gate", CLOSURE_GATES)
def test_closure_requires_every_completion_gate(gate: str) -> None:
    """Reject closure that omits a required completion proof."""
    marker = f"### {gate} gate\n"
    plan = complete_plan().replace(marker, f"### Missing {gate} gate\n", 1)
    assert f"closure missing {gate} gate" in validator()(plan, ledger())


@pytest.mark.parametrize("gate", CLOSURE_GATES)
def test_closure_gates_require_their_exact_completion_proofs(gate: str) -> None:
    """Reject named closure gates that omit their required proof."""
    plan = complete_plan().replace(CLOSURE_DETAILS[gate], "Proof omitted.", 1)
    assert f"closure has invalid {gate} gate" in validator()(plan, ledger())


@pytest.mark.parametrize(
    ("gate", "command"),
    [
        (gate, command)
        for gate, commands in REQUIRED_CLOSURE_COMMANDS.items()
        for command in commands
    ],
)
def test_closure_commands_are_exact_and_executable(gate: str, command: str) -> None:
    """Reject closure prose that lacks the exact artifact-producing command."""
    marker = f"### {gate} gate\n"
    prefix, closure = complete_plan().split(marker, 1)
    plan = prefix + marker + closure.replace(f"- `{command}`\n", "", 1)
    assert f"closure missing required {gate} commands" in validator()(plan, ledger())


def test_repository_quality_rejects_duplicate_module_mypy_discovery() -> None:
    """Reject the repository-wide path form that discovers modules twice."""
    valid = "- `uv run mypy`\n"
    invalid = valid + "- `uv run mypy .`\n"
    plan = complete_plan().replace(valid, invalid, 1)
    assert "closure has invalid Repository quality commands" in validator()(
        plan, ledger()
    )


def test_closure_proofs_must_be_markdown_list_items() -> None:
    """Reject closure prose outside the required gate list structure."""
    detail = CLOSURE_DETAILS["Package"]
    plan = complete_plan().replace(f"- {detail}", detail, 1)
    assert "closure has invalid Package gate" in validator()(plan, ledger())


def test_publication_boundary_is_an_action_limit_not_a_fake_proof() -> None:
    """Reject a local-log command presented as proof of remote publication state."""
    detail = CLOSURE_DETAILS["Publication boundary"]
    plan = complete_plan().replace(
        detail,
        "Use a local branch log to prove that no commit or tag was pushed.",
        1,
    )
    assert "closure has invalid Publication boundary gate" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("path", ENTITY_SHELL_FILES)
def test_canonical_entity_shells_precede_every_member_owner(path: str) -> None:
    """Reject a declaring type scheduled after members or return sites use it."""
    plan = complete_plan().replace(f"- `{path}`\n", "", 1)
    assert "declaring type unavailable before member ownership" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    ("component", "path"),
    [
        (component, path)
        for component, paths in ENTITY_FRAGMENT_FILES.items()
        for path in paths
    ],
)
def test_entity_members_have_distinct_owned_partial_fragments(
    component: int,
    path: str,
) -> None:
    """Reject member slices that silently edit another component's entity file."""
    section = component_section(component)
    invalid = section.replace(f"- `{path}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert f"component {component} missing entity partial Files" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("path", DIAGNOSTIC_SHARED_FILES)
def test_diagnostics_declares_every_instrumented_path(path: str) -> None:
    """Reject logging work that cannot stage every instrumented production file."""
    section = component_section(17)
    invalid = section.replace(f"- `{path}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 17 has invalid Shared files" in validator()(plan, ledger())


def test_no_build_commands_are_explicitly_release_configuration() -> None:
    """Reject tests or examples that run stale Debug output after a Release build."""
    plan = complete_plan().replace(
        "--configuration Release --framework net8.0 --no-build",
        "--framework net8.0 --no-build",
        1,
    )
    assert "component 1 has non-Release --no-build command" in validator()(
        plan, ledger()
    )


def test_json_unit_tests_reference_the_adapter_and_share_the_lock_graph() -> None:
    """Reject C9 wiring that leaves QueryJsonTests unable to compile or restore."""
    section = component_section(9)
    reference = PROJECT_WIRING[9][-1]
    invalid = section.replace(f"- `{reference}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 9 has invalid Project wiring" in validator()(plan, ledger())

    lock_path = "tests/LibTmux.UnitTests/packages.lock.json"
    invalid = section.replace(f"- `{lock_path}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 9 has invalid Shared files" in validator()(plan, ledger())


def test_local_package_wiring_is_cpm_correct_and_exact() -> None:
    """Reject inline versions, loose ranges, or an inexact JSON-to-core dependency."""
    section = component_section(18)
    central = PROJECT_WIRING[18][2]
    inline = PROJECT_WIRING[18][3]
    dependency = PROJECT_WIRING[18][5]
    mutations = (
        section.replace("[0.1.0-local]", "0.1.0-local", 1),
        section.replace(inline, inline.replace("versionless ", ""), 1),
        section.replace(dependency, dependency.replace("exactly ", "at least "), 1),
    )
    assert central in section
    for invalid in mutations:
        plan = complete_plan().replace(section, invalid, 1)
        assert "component 18 has invalid Project wiring" in validator()(plan, ledger())


@pytest.mark.parametrize(
    "path",
    (
        "Directory.Packages.props",
        "src/LibTmux/packages.lock.json",
        "src/LibTmux.Query.Json/packages.lock.json",
    ),
)
def test_packaging_shares_central_versions_and_shipping_locks(path: str) -> None:
    """Reject package closure that cannot stage its central or locked restore edits."""
    section = component_section(18)
    invalid = section.replace(f"- `{path}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 18 has invalid Shared files" in validator()(plan, ledger())


def test_locked_linux_restore_precedes_every_aot_no_restore_publish() -> None:
    """Reject a runtime publish that consumes no runtime-specific locked assets."""
    section = component_section(18)
    restore = f"- `{AOT_COMMANDS[0]}`"
    publish = f"- `{AOT_COMMANDS[1]}`"
    invalid = section.replace(restore, "restore-marker", 1)
    invalid = invalid.replace(publish, restore, 1).replace("restore-marker", publish, 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 18 has invalid AOT restore ordering" in validator()(
        plan, ledger()
    )


def test_workflow_checks_claim_configuration_not_runtime_execution() -> None:
    """Reject closure language that upgrades YAML validation into runtime proof."""
    detail = CLOSURE_DETAILS["Platform workflow configuration"]
    invalid_detail = detail.replace(
        "this local check does not execute those runtime jobs",
        "this local check proves those runtime jobs passed",
    )
    plan = complete_plan().replace(detail, invalid_detail, 1)
    assert "closure has invalid Platform workflow configuration gate" in validator()(
        plan, ledger()
    )


def test_external_workflow_runtime_evidence_is_an_explicit_handoff() -> None:
    """Reject closure that implies local workflow inspection produced CI evidence."""
    detail = CLOSURE_DETAILS["External workflow evidence"]
    plan = complete_plan().replace(
        detail,
        "Local workflow configuration is complete runtime evidence.",
        1,
    )
    assert "closure has invalid External workflow evidence gate" in validator()(
        plan, ledger()
    )


def test_staged_scope_comparison_requires_exact_coverage() -> None:
    """Compare staged paths with every declared file and directory allow-root."""
    compare = t.cast(
        t.Callable[[t.Iterable[str], t.Iterable[str]], list[str]],
        validator_namespace()["compare_staged_scope"],
    )
    allowed = ["a.cs", "evidence/final"]
    staged = ["a.cs", "evidence/final/environment.json"]
    assert compare(allowed, staged) == []
    assert compare(allowed, [*staged, "outside.txt"]) == [
        "staged paths do not exactly match component allow-list"
    ]
    assert compare(allowed, ["a.cs"]) == [
        "staged paths do not exactly match component allow-list"
    ]


def test_stage_compare_commit_and_clean_index_are_ordered_checkpoints() -> None:
    """Reject a component that commits before scope proof or leaves staged residue."""
    section = component_section(1)
    verify_scope = next(
        line for line in section.splitlines() if "--verify-staged-scope" in line
    )
    commit = f"- `{atomic_commit_command(1)}`"
    clean = '- `test -z "$(git diff --cached --name-only)"`'
    invalid = section.replace(verify_scope, "scope-marker", 1)
    invalid = invalid.replace(commit, verify_scope, 1).replace(
        "scope-marker", commit, 1
    )
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 1 has invalid commit checkpoint order" in validator()(
        plan, ledger()
    )

    invalid = section.replace(clean, "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 1 missing clean-index checkpoint" in validator()(plan, ledger())


def test_atomic_commit_command_matches_the_declared_message() -> None:
    """Reject a commit command that can produce a message other than the plan."""
    section = component_section(1)
    invalid = section.replace(
        atomic_commit_command(1),
        atomic_commit_command(1).replace("approved behavior", "different behavior"),
        1,
    )
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 1 missing exact Atomic commit command" in validator()(
        plan, ledger()
    )


def test_package_inspection_covers_sourcelink_revision_and_privacy() -> None:
    """Reject archive listing without semantic metadata and privacy inspection."""
    section = component_section(18)
    command = PACKAGE_COMMANDS[-1]
    invalid = section.replace(command, command.replace(" --repository .", ""), 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 18 missing required Full gate commands" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    ("graph", "pair"),
    C18_RESTORE_PAIRS.items(),
)
def test_component_eighteen_generates_each_lock_graph_before_locked_restore(
    graph: str,
    pair: tuple[str, str],
) -> None:
    """Reject locked consumption without generation for a changed C18 graph."""
    del graph
    section = component_section(18)
    invalid = section.replace(f"- `{pair[0]}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 18 has invalid NuGet lock generation" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    ("graph", "pair"),
    C18_RESTORE_PAIRS.items(),
)
def test_component_eighteen_lock_pairs_are_immediate_and_identical(
    graph: str,
    pair: tuple[str, str],
) -> None:
    """Reject interleaving or graph drift between generation and consumption."""
    del graph
    section = component_section(18)
    locked = f"- `{pair[1]}`\n"
    invalid = section.replace(locked, "- `dotnet --info`\n" + locked, 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 18 has invalid NuGet lock generation" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    ("path", "message"),
    (
        (
            "src/LibTmux/packages.lock.json",
            "component 18 has invalid Shared files",
        ),
        (
            "src/LibTmux.Query.Json/packages.lock.json",
            "component 18 has invalid Shared files",
        ),
        (
            "src/LibTmux.Query.Json/packages.packed.lock.json",
            "component 18 has invalid Files inventory",
        ),
        (
            "tests/LibTmux.PackageConsumer/packages.lock.json",
            "component 18 has invalid Files inventory",
        ),
        (
            "examples/LibTmux.Examples/packages.lock.json",
            "component 18 has invalid Files inventory",
        ),
        (
            "tests/LibTmux.AotSmoke/packages.lock.json",
            "component 18 has invalid Files inventory",
        ),
    ),
)
def test_component_eighteen_owns_every_generated_lock(path: str, message: str) -> None:
    """Reject a generated graph whose lock cannot enter the atomic source commit."""
    section = component_section(18)
    invalid = section.replace(f"- `{path}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert message in validator()(plan, ledger())


def test_final_matrix_evidence_is_source_bound_before_component_closure() -> None:
    """Reject retained evidence generated after the phase and staging checkpoints."""
    section = component_section(18)
    phase = next(
        line
        for line in section.splitlines()
        if "--phase component --component 18 " in line
        and "--print-stage-paths" not in line
        and "--verify-staged-scope" not in line
    )
    matrix = f"- `{FINAL_MATRIX_COMMANDS[0]}`"
    invalid = section.replace(matrix, "matrix-marker", 1)
    invalid = invalid.replace(phase, matrix, 1).replace("matrix-marker", phase, 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 18 has invalid final evidence ordering" in validator()(
        plan, ledger()
    )


def test_public_api_types_bind_to_exact_owned_production_files() -> None:
    """Cross-check renamed request contracts against their planned source owners."""
    cross_check = t.cast(
        t.Callable[[str, dict[str, t.Any]], list[str]],
        validator_namespace()["validate_public_api_files"],
    )
    assert cross_check(complete_plan(), public_api()) == []

    path = PUBLIC_API_FILE_BINDINGS["T:LibTmux.DisplayPopupRequest"][1]
    invalid_plan = complete_plan().replace(f"- `{path}`\n", "", 1)
    assert "public API production file missing or misowned" in cross_check(
        invalid_plan, public_api()
    )

    invalid_api = public_api()
    invalid_api["types"] = invalid_api["types"][:-1]
    assert "planned production type missing from public API" in cross_check(
        complete_plan(), invalid_api
    )


def test_component_three_owns_tmux_enums_and_all_version_matrix_tests() -> None:
    """Keep enum ownership and version evidence aligned with their source contracts."""
    namespace = validator_namespace()
    reconciler = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "reconcile_versions.py")
    )
    version_methods = tuple(
        f"VersionParityTests.{method}"
        for method in t.cast(
            dict[str, str], reconciler["VERSION_PARITY_METHODS"]
        ).values()
    )
    enum_types = (
        "T:LibTmux.OptionScope",
        "T:LibTmux.PaneDirection",
        "T:LibTmux.ResizeDirection",
        "T:LibTmux.WindowDirection",
    )
    expected_red_tests = (
        "TmuxCapabilitiesTests.Comparisons_cover_equal_older_and_newer_versions",
        *version_methods,
        "VersionParityTests.CommandFlags",
    )

    assert len(version_methods) == 41
    assert namespace["REQUIRED_RED_TESTS"][3] == expected_red_tests
    assert all(type_id in namespace["COMPONENT_API_TYPES"][3] for type_id in enum_types)
    assert all(
        type_id not in namespace["COMPONENT_API_TYPES"][11] for type_id in enum_types
    )
    assert all(
        type_id not in namespace["COMPONENT_API_TYPES"][12] for type_id in enum_types
    )
    assert all(
        type_id not in namespace["COMPONENT_API_TYPES"][14] for type_id in enum_types
    )
    assert all(
        namespace["PUBLIC_API_FILE_BINDINGS"][type_id]
        == (3, "src/LibTmux/Constants/TmuxEnums.cs")
        for type_id in enum_types
    )
    assert namespace["COMPONENT_DEPENDENCIES"][11] == (
        "component 3",
        "component 10",
    )
    assert namespace["COMPONENT_DEPENDENCIES"][12] == (
        "component 3",
        "component 10",
        "component 11",
    )

    plan_path = production_plan_path()
    components, _ = t.cast(
        t.Callable[[str], tuple[list[dict[str, t.Any]], dict[str, t.Any]]],
        namespace["parse_markdown"],
    )(plan_path.read_text(encoding="utf-8"))
    component = next(component for component in components if component["id"] == 3)
    red_lines = t.cast(dict[str, list[list[str]]], component["fields"])[
        "Red behavioral test"
    ][0]
    listed_version_methods = tuple(
        line.split("`", 2)[1] for line in red_lines if "`VersionParityTests." in line
    )

    assert listed_version_methods == (
        *version_methods,
        "VersionParityTests.CommandFlags",
    )


def test_component_four_governance_is_exact() -> None:
    """Bind C4 files, shared seams, APIs, rows, and named behavioral tests."""
    namespace = validator_namespace()
    assert namespace["COMPONENT_FILES"][4] == COMPONENT_FILES[4]
    assert namespace["COMPONENT_SHARED_FILES"][4] == COMPONENT_SHARED_FILES[4]
    assert namespace["COMPONENT_API_TYPES"][4] == COMPONENT_API_TYPES[4]
    assert namespace["C4_MATERIALIZATION_CONTRACT"] == C4_MATERIALIZATION_CONTRACT
    assert namespace["REQUIRED_RED_TESTS"][4] == REQUIRED_RED_TESTS[4]
    assert {
        type_id: namespace["PUBLIC_API_FILE_BINDINGS"][type_id]
        for type_id in COMPONENT_API_TYPES[4]
    } == {
        "T:LibTmux.Internal.FormatProjection": (
            4,
            "src/LibTmux/Materialization/FormatProjection.cs",
        ),
        "T:LibTmux.Internal.SeparatedRowFramer": (
            4,
            "src/LibTmux/Materialization/SeparatedRowFramer.cs",
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
    }

    plan_path = production_plan_path()
    components, _ = t.cast(
        t.Callable[[str], tuple[list[dict[str, t.Any]], dict[str, t.Any]]],
        namespace["parse_markdown"],
    )(plan_path.read_text(encoding="utf-8"))
    by_id = {component["id"]: component for component in components}
    c4_fields = t.cast(dict[str, list[list[str]]], by_id[4]["fields"])
    list_tokens = t.cast(
        t.Callable[[list[str]], list[str] | None], namespace["list_tokens"]
    )
    assert tuple(list_tokens(c4_fields["Files"][0]) or ()) == COMPONENT_FILES[4]
    assert (
        tuple(list_tokens(c4_fields["Shared files"][0]) or ())
        == (COMPONENT_SHARED_FILES[4])
    )
    assert (
        tuple(list_tokens(c4_fields["API owners"][0]) or ()) == (COMPONENT_API_TYPES[4])
    )
    assert tuple(list_tokens(c4_fields["Materialization contract"][0]) or ()) == (
        C4_MATERIALIZATION_CONTRACT
    )

    current_ledger = t.cast(dict[str, t.Any], namespace["load_ledger"]())
    expected_rows = {
        row["pythonSymbolId"]
        for row in current_ledger["rows"]
        if row["componentId"] == 4
    }
    planned_c4_rows = set(list_tokens(c4_fields["Ledger rows"][0]) or ())
    planned_c2_rows = set(
        list_tokens(
            t.cast(dict[str, list[list[str]]], by_id[2]["fields"])["Ledger rows"][0]
        )
        or ()
    )
    moved = {
        "libtmux.pane:Pane.from_pane_id",
        "libtmux.window:Window.from_window_id",
    }
    assert len(planned_c4_rows) == 197
    assert planned_c4_rows == expected_rows
    assert len(planned_c2_rows) == 9
    assert moved <= planned_c4_rows
    assert moved.isdisjoint(planned_c2_rows)

    named_tests = tuple(
        line.split("`", 2)[1]
        for line in c4_fields["Red behavioral test"][0]
        if "`MaterializationTests." in line
    )
    assert named_tests == (
        RED_CASES[4][1],
        *REQUIRED_RED_TESTS[4],
    )


@pytest.mark.parametrize("contract", C4_MATERIALIZATION_CONTRACT)
def test_component_four_materialization_contract_is_frozen(contract: str) -> None:
    """Reject framing, identity, or Component 5 handoff contract drift."""
    section = component_section(4)
    invalid = section.replace(f"- `{contract}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 4 has invalid materialization contract" in validator()(
        plan,
        ledger(),
    )


def test_component_four_ledger_validator_rejects_ownership_drift() -> None:
    """Reject count, ownership, and parity-test drift in C4 lookup rows."""
    namespace = validator_namespace()
    validate_c4 = t.cast(
        t.Callable[[dict[str, t.Any]], list[str]],
        namespace["validate_c4_ledger_ownership"],
    )
    current = t.cast(dict[str, t.Any], namespace["load_ledger"]())
    assert validate_c4(current) == []

    invalid = copy.deepcopy(current)
    lookup = next(
        row
        for row in invalid["rows"]
        if row["pythonSymbolId"] == "libtmux.pane:Pane.from_pane_id"
    )
    lookup["testPath"] = "tests/LibTmux.IntegrationTests/Parity/Other.cs"
    assert validate_c4(invalid) == ["C4 lookup ledger ownership drifted"]

    reassigned = copy.deepcopy(current)
    next(
        row
        for row in reassigned["rows"]
        if row["pythonSymbolId"] == "libtmux.window:Window.from_window_id"
    )["componentId"] = 5
    assert validate_c4(reassigned) == ["C4 lookup ledger ownership drifted"]


def test_component_three_owns_the_server_version_fragment() -> None:
    """Keep the approved Server.Version member buildable in its owning slice."""
    namespace = validator_namespace()
    expected_path = "src/LibTmux/Server.Version.cs"
    member_id = "P:LibTmux.Server.Version"

    assert expected_path in namespace["COMPONENT_FILES"][3]
    assert namespace["ENTITY_FRAGMENT_FILES"][3] == (expected_path,)
    assert namespace["COMPONENT_DEPENDENCIES"][3] == (
        "component 1",
        "component 2",
    )
    assert namespace["PUBLIC_API_MEMBER_FILE_BINDINGS"][member_id] == (
        3,
        expected_path,
    )

    cross_check = t.cast(
        t.Callable[[str, dict[str, t.Any]], list[str]],
        namespace["validate_public_api_files"],
    )
    assert cross_check(complete_plan(), public_api()) == []

    invalid_plan = complete_plan().replace(f"- `{expected_path}`\n", "", 1)
    assert "public API member production file missing or misowned" in cross_check(
        invalid_plan,
        public_api(),
    )

    invalid_api = public_api()
    invalid_api["members"] = [
        member for member in invalid_api["members"] if member["id"] != member_id
    ]
    assert "planned public member missing from public API" in cross_check(
        complete_plan(),
        invalid_api,
    )


def test_component_three_requires_exact_tmux_37_transition_evidence() -> None:
    """Reject a transition proof that loses a build, record, or reconciliation gate."""
    expected_shared_files = (
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
    assert validator()(complete_plan(), ledger()) == []

    section = component_section(3)
    for path in expected_shared_files:
        invalid = section.replace(f"- `{path}`\n", "", 1)
        plan = complete_plan().replace(section, invalid, 1)
        assert "component 3 has invalid Shared files" in validator()(plan, ledger())

    for contract in TMUX_37_TRANSITION_PROOF_CONTRACT:
        invalid = section.replace(f"- `{contract}`\n", "", 1)
        plan = complete_plan().replace(section, invalid, 1)
        assert "component 3 has invalid tmux 3.7 transition proof" in validator()(
            plan,
            ledger(),
        )

    for command in REQUIRED_GATE_COMMANDS[3]:
        invalid = section.replace(f"- `{command}`\n", "", 1)
        plan = complete_plan().replace(section, invalid, 1)
        assert "component 3 missing required Full gate commands" in validator()(
            plan,
            ledger(),
        )


def test_future_operation_components_own_version_policy_evidence() -> None:
    """Require wrapper owners to carry the proof, not the policy documents."""
    namespace = validator_namespace()

    assert namespace["VERSION_POLICY_OWNER_COMPONENTS"] == (
        10,
        11,
        12,
        13,
        15,
        16,
    )
    assert namespace["VERSION_POLICY_SHARED_FILES"] == VERSION_POLICY_SHARED_FILES
    for component_id in VERSION_POLICY_OWNER_COMPONENTS:
        assert not any(
            path in namespace["COMPONENT_SHARED_FILES"][component_id]
            for path in VERSION_POLICY_SHARED_FILES
        )
        assert component_id in namespace["VERSION_POLICY_PROOFS_BY_COMPONENT"]


def test_future_operation_components_freeze_each_wrapper_policy_proof() -> None:
    """Reject an owner section that drops an exact test, behavior, or pending status."""
    namespace = validator_namespace()
    assert namespace["VERSION_POLICY_PROOFS_BY_COMPONENT"] == (
        VERSION_POLICY_PROOFS_BY_COMPONENT
    )

    for component_id, proofs in VERSION_POLICY_PROOFS_BY_COMPONENT.items():
        section = component_section(component_id)
        for proof in proofs:
            invalid = section.replace(f"- `{proof}`\n", "", 1)
            plan = complete_plan().replace(section, invalid, 1)
            assert (
                f"component {component_id} has invalid version policy proofs"
                in validator()(plan, ledger())
            )


def test_component_three_rejects_each_missing_version_matrix_method() -> None:
    """Reject removal or addition of one Component 3 version-evidence method."""
    reconciler = runpy.run_path(
        str(pathlib.Path(__file__).parents[1] / "reconcile_versions.py")
    )
    version_methods = tuple(
        f"VersionParityTests.{method}"
        for method in t.cast(
            dict[str, str], reconciler["VERSION_PARITY_METHODS"]
        ).values()
    )
    section = component_section(3)

    for method in version_methods:
        assert f"`{method}`" in section
        invalid = section.replace(f"- `{method}` must fail", "- `omitted` must fail", 1)
        plan = complete_plan().replace(section, invalid, 1)
        assert "component 3 has invalid Red behavioral tests" in validator()(
            plan, ledger()
        )

    invalid = section.replace(
        "### RED command\n",
        "- `VersionParityTests.Unrelated` must fail before production code is added.\n\n"
        "### RED command\n",
        1,
    )
    plan = complete_plan().replace(section, invalid, 1)
    assert "component 3 has invalid Red behavioral tests" in validator()(plan, ledger())


def test_component_fifteen_requires_option_scope_owner_dependency() -> None:
    """Reject hooks work that omits its directly exposed enum owner."""
    expected_dependencies = (
        "component 1",
        "component 2",
        "component 3",
        "component 14",
    )
    assert COMPONENT_DEPENDENCIES[15] == expected_dependencies

    section = component_section(15)
    assert "- `component 3`\n" in section
    invalid = section.replace("- `component 3`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)

    assert "component 15 has invalid Depends on" in validator()(plan, ledger())


def test_component_two_owns_its_color_mode_dependency() -> None:
    """Keep ServerConnectionOptions independently buildable in Component 2."""
    namespace = validator_namespace()
    plan_path = production_plan_path()
    components, _ = t.cast(
        t.Callable[[str], tuple[list[dict[str, t.Any]], dict[str, t.Any]]],
        namespace["parse_markdown"],
    )(plan_path.read_text(encoding="utf-8"))
    by_id = {component["id"]: component for component in components}
    list_tokens = t.cast(
        t.Callable[[list[str]], list[str] | None], namespace["list_tokens"]
    )

    component_two = t.cast(dict[str, list[list[str]]], by_id[2]["fields"])
    component_three = t.cast(dict[str, list[list[str]]], by_id[3]["fields"])
    component_two_files = list_tokens(component_two["Files"][0])
    component_three_files = list_tokens(component_three["Files"][0])
    component_two_apis = list_tokens(component_two["API owners"][0])
    component_three_apis = list_tokens(component_three["API owners"][0])

    assert component_two_files is not None
    assert component_three_files is not None
    assert component_two_apis is not None
    assert component_three_apis is not None
    assert "src/LibTmux/TmuxColorMode.cs" in component_two_files
    assert "src/LibTmux/TmuxColorMode.cs" not in component_three_files
    assert "T:LibTmux.TmuxColorMode" in component_two_apis
    assert "T:LibTmux.TmuxColorMode" not in component_three_apis


def test_server_projection_ledger_rows_are_owned_by_component_four() -> None:
    """Keep projection-only parity evidence with its materializer owner."""
    namespace = validator_namespace()
    plan_path = production_plan_path()
    components, _ = t.cast(
        t.Callable[[str], tuple[list[dict[str, t.Any]], dict[str, t.Any]]],
        namespace["parse_markdown"],
    )(plan_path.read_text(encoding="utf-8"))
    by_id = {component["id"]: component for component in components}
    list_tokens = t.cast(
        t.Callable[[list[str]], list[str] | None], namespace["list_tokens"]
    )
    load_ledger = t.cast(t.Callable[[], dict[str, t.Any]], namespace["load_ledger"])
    row_ids = (
        "libtmux.server:Server.child_id_attribute",
        "libtmux.server:Server.formatter_prefix",
    )
    expected_test_path = (
        "tests/LibTmux.IntegrationTests/Parity/Component04ParityTests.cs"
    )
    rows = {
        row["pythonSymbolId"]: row
        for row in load_ledger()["rows"]
        if row.get("pythonSymbolId") in row_ids
    }
    component_two_rows = list_tokens(
        t.cast(dict[str, list[list[str]]], by_id[2]["fields"])["Ledger rows"][0]
    )
    component_four_rows = list_tokens(
        t.cast(dict[str, list[list[str]]], by_id[4]["fields"])["Ledger rows"][0]
    )

    assert component_two_rows is not None
    assert component_four_rows is not None
    assert set(rows) == set(row_ids)
    for row_id in row_ids:
        assert row_id not in component_two_rows
        assert row_id in component_four_rows
        assert rows[row_id]["componentId"] == 4
        assert rows[row_id]["testPath"] == expected_test_path


@pytest.mark.parametrize("path", FORBIDDEN_PRODUCTION_FILES)
def test_stale_or_unapproved_production_files_are_rejected(path: str) -> None:
    """Reject superseded request and internal harness destinations."""
    section = component_section(13)
    invalid = section.replace("### Files\n\n", f"### Files\n\n- `{path}`\n", 1)
    plan = complete_plan().replace(section, invalid, 1)
    assert "plan contains stale or unapproved production Files" in validator()(
        plan, ledger()
    )


def test_components_are_frozen_in_execution_order() -> None:
    """Reject a valid set of components presented out of dependency order."""
    first = component_section(1)
    second = component_section(2)
    plan = complete_plan().replace(
        first + "\n" + second,
        second + "\n" + first,
        1,
    )

    assert "component sections are out of frozen order" in validator()(plan, ledger())


@pytest.mark.parametrize("component", COMPONENT_IDS)
def test_every_component_has_an_exact_complete_files_inventory(component: int) -> None:
    """Reject deletion of an otherwise non-special owned file."""
    section = component_section(component)
    path = COMPONENT_FILES[component][0]
    invalid = section.replace(f"- `{path}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)

    assert f"component {component} has invalid Files inventory" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize(
    "command",
    (
        "git push origin csharp",
        "git tag v0.1.0",
        "git tag -a v0.1.0 -m release",
    ),
)
def test_publication_mutations_are_rejected_globally(command: str) -> None:
    """Reject push and tag creation regardless of the section containing them."""
    marker = "- `git diff --check`\n"
    plan = complete_plan().replace(marker, marker + f"- `{command}`\n", 1)

    assert "plan contains forbidden publication command" in validator()(plan, ledger())


def test_behavioral_gates_precede_phase_validation() -> None:
    """Reject validating component state before its build has passed."""
    section = component_section(1)
    build = f"- `{PUBLIC_API_BUILD_COMMAND}`"
    phase = next(
        line
        for line in section.splitlines()
        if "--phase component --component 1 " in line
        and "--print-stage-paths" not in line
        and "--verify-staged-scope" not in line
    )
    invalid = section.replace(build, "build-marker", 1)
    invalid = invalid.replace(phase, build, 1).replace("build-marker", phase, 1)
    plan = complete_plan().replace(section, invalid, 1)

    assert "component 1 validates phase before behavioral gates" in validator()(
        plan, ledger()
    )


def test_all_dotnet_build_commands_are_release_configuration() -> None:
    """Reject an executable build that silently falls back to Debug."""
    invalid_build = PUBLIC_API_BUILD_COMMAND.replace(" --configuration Release", "")
    plan = complete_plan().replace(PUBLIC_API_BUILD_COMMAND, invalid_build, 1)

    assert "component 1 has non-Release dotnet build command" in validator()(
        plan, ledger()
    )


def test_each_component_requires_an_executable_red_command_and_trx_evidence() -> None:
    """Reject prose-only RED declarations that cannot prove a selected test failed."""
    section = component_section(1)
    without_command = section.replace(f"- `{RED_COMMANDS[1]}`\n", "", 1)
    without_evidence = section.replace(f"- `{RED_EVIDENCE[1]}`\n", "", 1)
    plan = complete_plan()

    assert "component 1 missing executable RED command" in validator()(
        plan.replace(section, without_command, 1), ledger()
    )
    assert "component 1 missing RED evidence" in validator()(
        plan.replace(section, without_evidence, 1), ledger()
    )


def test_query_json_requires_distinct_default_and_packed_lock_graphs() -> None:
    """Reject reusing one NuGet lock for conditional project/package graphs."""
    section = component_section(18)
    invalid = section.replace(
        "- `src/LibTmux.Query.Json/packages.packed.lock.json`\n",
        "",
        1,
    )
    plan = complete_plan().replace(section, invalid, 1)

    assert "component 18 missing packed Query.Json lock graph" in validator()(
        plan, ledger()
    )


def test_final_evidence_follows_a_clean_source_commit_and_gets_its_own_commit() -> None:
    """Reject retained evidence captured from mutable pre-commit source state."""
    section = component_section(18)
    source_commit = f"- `{atomic_commit_command(18)}`"
    retained_matrix = f"- `{RETAINED_MATRIX_COMMAND}`"
    invalid = section.replace(source_commit, "source-marker", 1)
    invalid = invalid.replace(retained_matrix, source_commit, 1).replace(
        "source-marker", retained_matrix, 1
    )
    plan = complete_plan().replace(section, invalid, 1)

    assert "component 18 has invalid source/evidence closure ordering" in validator()(
        plan, ledger()
    )


def test_component_three_evidence_follows_its_clean_source_commit() -> None:
    """Reject retained cohort 0001 captured from mutable Component 3 source."""
    section = component_section(3)
    source_commit = f"- `{atomic_commit_command(3)}`"
    retained_matrix = f"- `{C3_RETAINED_MATRIX_COMMAND}`"
    invalid = section.replace(source_commit, "source-marker", 1)
    invalid = invalid.replace(retained_matrix, source_commit, 1).replace(
        "source-marker", retained_matrix, 1
    )
    plan = complete_plan().replace(section, invalid, 1)

    assert "component 3 has invalid source/evidence closure ordering" in validator()(
        plan, ledger()
    )


@pytest.mark.parametrize("command", C3_EVIDENCE_COMMANDS)
def test_component_three_requires_complete_two_root_evidence_closure(
    command: str,
) -> None:
    """Reject loss of one retained, reconciliation, scope, or binding checkpoint."""
    section = component_section(3)
    invalid = section.replace(f"- `{command}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)

    assert "component 3 has invalid source/evidence closure ordering" in validator()(
        plan, ledger()
    )


def test_source_binding_constrains_the_descendant_diff_to_final_evidence() -> None:
    """Require parent fingerprint verification and one allowed evidence-only diff."""
    section = component_section(18)
    invalid = section.replace(
        POSTCOMMIT_SOURCE_BINDING_COMMAND,
        POSTCOMMIT_SOURCE_BINDING_COMMAND.replace(
            "--require-descendant-root", "--allow-descendant-root"
        ),
        1,
    )
    plan = complete_plan().replace(section, invalid, 1)

    assert "component 18 has incomplete source-binding closure" in validator()(
        plan, ledger()
    )


def test_every_public_api_type_has_one_frozen_component_owner() -> None:
    """Reject a plan whose API ownership covers only selected special types."""
    cross_check = t.cast(
        t.Callable[[str, dict[str, t.Any]], list[str]],
        validator_namespace()["validate_public_api_files"],
    )

    section = component_section(12)
    type_id = COMPONENT_API_TYPES[12][0]
    invalid = section.replace(f"- `{type_id}`\n", "", 1)
    plan = complete_plan().replace(section, invalid, 1)

    assert "public API type ownership incomplete or drifted" in cross_check(
        plan, public_api()
    )


def test_a_declared_red_test_the_repository_defines_is_accepted() -> None:
    """A proof that exists satisfies the check, in either language."""
    namespace = validator_namespace()

    assert namespace["defines_test"]("RequireRedTests.Rejects_successful_test_run"), (
        "a Python proof written in snake_case should count"
    )
    assert namespace["defines_test"](
        "SnapshotCollectionTests.Enumeration_is_local_and_uses_BCL_cardinality"
    ), "a C# proof should count"


def test_a_declared_red_test_nothing_defines_is_reported() -> None:
    """A plan promising a proof nobody wrote is what this check exists for."""
    namespace = validator_namespace()

    assert not namespace["defines_test"]("NoSuchTests.Nothing_defines_this_one")


def test_the_two_languages_compare_equal() -> None:
    """One proof written in each language's convention is one proof."""
    namespace = validator_namespace()
    comparable = namespace["comparable_test_name"]

    assert comparable("Rejects_stale_trx") == comparable("test_rejects_stale_trx")
