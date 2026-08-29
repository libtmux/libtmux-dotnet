#!/usr/bin/env bash
set -euo pipefail

readonly REQUIRED_VERSIONS=(3.2a 3.3a 3.4 3.5 3.6 3.7a 3.7b 3.7c)
readonly FRAMEWORKS=(net10.0 net8.0)
readonly COMPONENT_THREE_COHORT=0001
readonly CLOSURE_COHORT=closure
readonly TRANSITION_VERSION=3.7
readonly SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly CSHARP_DIRECTORY="$(cd -- "${SCRIPT_DIRECTORY}/../.." && pwd)"
readonly REPOSITORY_DIRECTORY="$(git -C "${CSHARP_DIRECTORY}" rev-parse --show-toplevel)"
platform_name=
case "$(uname -s)" in
    Linux) platform_name=linux ;;
    Darwin) platform_name=macos ;;
    *)
        echo "matrix runner requires Linux or macOS" >&2
        exit 1
        ;;
esac
readonly PLATFORM_NAME="${platform_name}"

include_master=0
evidence_directory=
capability_cohort=
while [[ $# -gt 0 ]]; do
    case "$1" in
        --include-master-advisory)
            include_master=1
            shift
            ;;
        --evidence-dir)
            if [[ $# -lt 2 ]]; then
                echo "--evidence-dir requires a path" >&2
                exit 2
            fi
            evidence_directory="$2"
            shift 2
            ;;
        --capability-cohort)
            if [[ $# -lt 2 ]]; then
                echo "--capability-cohort requires an identifier" >&2
                exit 2
            fi
            capability_cohort="$2"
            shift 2
            ;;
        --*)
            echo "unknown option: $1" >&2
            exit 2
            ;;
        *)
            break
            ;;
    esac
done
if [[ $# -ne 1 ]]; then
    echo "usage: run-matrix.sh [--include-master-advisory] [--evidence-dir PATH] [--capability-cohort 0001|closure] PROJECT" >&2
    exit 2
fi
readonly PROJECT="$1"
if [[ ! -f "${CSHARP_DIRECTORY}/${PROJECT}" ]]; then
    echo "test project does not exist: ${PROJECT}" >&2
    exit 2
fi

transition_proof=0
if [[ -n "${evidence_directory}" ]]; then
    if [[ "${evidence_directory}" == / || "${evidence_directory}" == . || "${evidence_directory}" == .. ]]; then
        echo "unsafe evidence directory: ${evidence_directory}" >&2
        exit 2
    fi
    if [[ "${evidence_directory}" != /* ]]; then
        evidence_directory="${CSHARP_DIRECTORY}/${evidence_directory}"
    fi
fi
if [[ -n "${capability_cohort}" ]]; then
    if [[ "${capability_cohort}" != "${COMPONENT_THREE_COHORT}" \
        && "${capability_cohort}" != "${CLOSURE_COHORT}" ]]; then
        echo "unknown capability cohort: ${capability_cohort}" >&2
        exit 2
    fi
    if [[ -z "${evidence_directory}" ]]; then
        echo "--capability-cohort requires --evidence-dir" >&2
        exit 2
    fi
    if [[ ${include_master} -eq 1 ]]; then
        echo "capability cohort ${capability_cohort} forbids master advisory lanes" >&2
        exit 2
    fi
    if [[ "${capability_cohort}" == "${COMPONENT_THREE_COHORT}" ]]; then
        transition_proof=1
    fi
fi

cd "${CSHARP_DIRECTORY}"
readonly SDK_VERSION="$(mise exec -- dotnet --version)"
if [[ "${SDK_VERSION}" != 10.0.302 ]]; then
    echo "expected .NET SDK 10.0.302, got ${SDK_VERSION}" >&2
    exit 1
fi

mise exec -- dotnet restore LibTmux.slnx --locked-mode
mise exec -- dotnet format LibTmux.slnx --verify-no-changes --no-restore
mise exec -- dotnet build LibTmux.slnx --configuration Release --no-restore --warnaserror

readonly EVALUATED_COMMIT="$(git -C "${REPOSITORY_DIRECTORY}" rev-parse HEAD)"
readonly EVALUATED_COMMIT_TREE="$(git -C "${REPOSITORY_DIRECTORY}" rev-parse 'HEAD^{tree}')"
source_exclusions=()
if [[ -n "${evidence_directory}" ]]; then
    source_exclusions=(--exclude-root "${evidence_directory}")
fi
readonly SOURCE_TREE_FINGERPRINT="$(uv run python eng/evidence/assemble_bundle.py \
    --source-fingerprint "${REPOSITORY_DIRECTORY}" \
    "${source_exclusions[@]}")"
readonly SOURCE_STATE="$(uv run python eng/evidence/assemble_bundle.py \
    --source-state "${REPOSITORY_DIRECTORY}" \
    "${source_exclusions[@]}")"
transition_tmux_binary=
transition_tmux_source_commit=
if [[ ${transition_proof} -eq 1 ]]; then
    transition_build_output="$("${SCRIPT_DIRECTORY}/build-version.sh" "${TRANSITION_VERSION}")"
    transition_tmux_binary="$(sed -n 's/^binary=//p' <<<"${transition_build_output}" | tail -1)"
    transition_tmux_source_commit="$(sed -n 's/^commit=//p' <<<"${transition_build_output}" | tail -1)"
    if [[ ! -x "${transition_tmux_binary}" \
        || ! "${transition_tmux_source_commit}" =~ ^[0-9a-f]{40}$ \
        || "$("${transition_tmux_binary}" -V)" != "tmux ${TRANSITION_VERSION}" ]]; then
        echo "invalid transition build-version output for ${TRANSITION_VERSION}" >&2
        exit 1
    fi
fi
readonly TRANSITION_TMUX_BINARY="${transition_tmux_binary}"
readonly TRANSITION_TMUX_SOURCE_COMMIT="${transition_tmux_source_commit}"
candidate=
ownership_nonce=
results_file=
if [[ -n "${evidence_directory}" ]]; then
    mkdir -p "$(dirname -- "${evidence_directory}")"
    candidate_contract="$(uv run python eng/evidence/assemble_bundle.py \
        --create-candidate \
        --output "${evidence_directory}")"
    candidate="$(jq -er '.candidate' <<<"${candidate_contract}")"
    ownership_nonce="$(jq -er '.ownershipNonce' <<<"${candidate_contract}")"
    mkdir -p "${candidate}/protocol-transcripts"
    results_file="${candidate}/results.ndjson"
fi

cleanup_candidate() {
    if [[ -n "${candidate}" && -d "${candidate}" ]]; then
        uv run python eng/evidence/assemble_bundle.py \
            --discard-candidate "${candidate}" \
            --output "${evidence_directory}" \
            --ownership-nonce "${ownership_nonce}"
    fi
}
trap cleanup_candidate EXIT

record_result() {
    local version="$1"
    local framework="$2"
    local status="$3"
    local advisory="$4"
    local source_commit="$5"
    local test_count="$6"
    if [[ -z "${results_file}" ]]; then
        return
    fi
    local source_commit_json=null
    if [[ "${source_commit}" != null ]]; then
        source_commit_json="$(jq -Rn --arg value "${source_commit}" '$value')"
    fi
    jq -cn \
        --arg evaluatedCommit "${EVALUATED_COMMIT}" \
        --arg framework "${framework}" \
        --arg status "${status}" \
        --argjson tmuxSourceCommit "${source_commit_json}" \
        --arg tmuxVersion "${version}" \
        --argjson advisory "${advisory}" \
        --argjson testCount "${test_count}" \
        '{advisory:$advisory,evaluatedCommit:$evaluatedCommit,framework:$framework,status:$status,testCount:$testCount,tmuxSourceCommit:$tmuxSourceCommit,tmuxVersion:$tmuxVersion}' \
        >> "${results_file}"
}

run_one() {
    local version="$1"
    local framework="$2"
    local binary="$3"
    local expected_version="$4"
    local source_commit="$5"
    local advisory="$6"
    local output_file
    output_file="$(mktemp)"
    local status=passed
    set +e
    LIBTMUX_TMUX="${binary}" \
        LIBTMUX_TMUX_SOURCE_COMMIT="${source_commit}" \
        LIBTMUX_EXPECTED_TMUX_VERSION="${expected_version}" \
        LIBTMUX_INTEGRATION_REQUIRED=1 \
        LIBTMUX_PROTOCOL_TRANSCRIPT_DIR="${candidate:+${candidate}/protocol-transcripts}" \
        LIBTMUX_TRANSITION_TMUX_3_7="${TRANSITION_TMUX_BINARY}" \
        LIBTMUX_TRANSITION_TMUX_3_7_SOURCE_COMMIT="${TRANSITION_TMUX_SOURCE_COMMIT}" \
        mise exec -- dotnet test \
        --project "${PROJECT}" \
        --configuration Release \
        --framework "${framework}" \
        --no-build \
        --minimum-expected-tests 1 \
        2>&1 | tee "${output_file}"
    local test_status=${PIPESTATUS[0]}
    set -e
    local test_count
    test_count="$(sed -nE 's/^[[:space:]]*total:[[:space:]]*([0-9]+).*/\1/p' "${output_file}" | tail -1)"
    local skipped
    skipped="$(sed -nE 's/^[[:space:]]*skipped:[[:space:]]*([0-9]+).*/\1/p' "${output_file}" | tail -1)"
    if [[ ${test_status} -ne 0 || -z "${test_count}" || "${test_count}" -eq 0 || -z "${skipped}" || "${skipped}" -ne 0 ]]; then
        status=failed
    fi
    rm -f -- "${output_file}"
    record_result "${version}" "${framework}" "${status}" "${advisory}" "${source_commit}" "${test_count:-0}"
    if [[ "${status}" != passed && "${advisory}" == false ]]; then
        return 1
    fi
    return 0
}

run_version() {
    local version="$1"
    local advisory="$2"
    local build_output
    if ! build_output="$("${SCRIPT_DIRECTORY}/build-version.sh" "${version}")"; then
        if [[ "${advisory}" == true ]]; then
            for framework in "${FRAMEWORKS[@]}"; do
                record_result "${version}" "${framework}" failed true null 0
            done
            return 0
        fi
        return 1
    fi
    local binary
    binary="$(sed -n 's/^binary=//p' <<<"${build_output}" | tail -1)"
    local source_commit
    source_commit="$(sed -n 's/^commit=//p' <<<"${build_output}" | tail -1)"
    if [[ ! -x "${binary}" || ! "${source_commit}" =~ ^[0-9a-f]{40}$ ]]; then
        echo "invalid build-version output for ${version}" >&2
        if [[ "${advisory}" == true ]]; then
            for framework in "${FRAMEWORKS[@]}"; do
                record_result "${version}" "${framework}" failed true null 0
            done
            return 0
        fi
        return 1
    fi
    local expected_version="${version}"
    if [[ "${version}" == master ]]; then
        if ! expected_version="$(${binary} -V | sed -n 's/^tmux //p')" \
            || [[ -z "${expected_version}" ]]; then
            for framework in "${FRAMEWORKS[@]}"; do
                record_result "${version}" "${framework}" failed true null 0
            done
            return 0
        fi
    fi
    for framework in "${FRAMEWORKS[@]}"; do
        run_one "${version}" "${framework}" "${binary}" "${expected_version}" "${source_commit}" "${advisory}"
    done
}

run_transition_one() {
    local version="$1"
    local framework="$2"
    local binary="$3"
    local source_commit="$4"
    local output_file
    output_file="$(mktemp)"
    set +e
    LIBTMUX_TMUX="${binary}" \
        LIBTMUX_TMUX_SOURCE_COMMIT="${source_commit}" \
        LIBTMUX_EXPECTED_TMUX_VERSION="${version}" \
        LIBTMUX_INTEGRATION_REQUIRED=1 \
        LIBTMUX_PROTOCOL_TRANSCRIPT_DIR="${candidate}/protocol-transcripts" \
        LIBTMUX_BREAK_PANE_TRANSITION_PROOF=1 \
        LIBTMUX_TRANSITION_TMUX_3_7="${TRANSITION_TMUX_BINARY}" \
        LIBTMUX_TRANSITION_TMUX_3_7_SOURCE_COMMIT="${TRANSITION_TMUX_SOURCE_COMMIT}" \
        LIBTMUX_TEST_FRAMEWORK="${framework}" \
        mise exec -- dotnet test \
        --project "${PROJECT}" \
        --configuration Release \
        --framework "${framework}" \
        --no-build \
        --minimum-expected-tests 1 \
        --filter-method LibTmux.IntegrationTests.Versioning.VersionParityTests.BreakPane37Workaround \
        2>&1 | tee "${output_file}"
    local test_status=${PIPESTATUS[0]}
    set -e
    local test_count
    test_count="$(sed -nE 's/^[[:space:]]*total:[[:space:]]*([0-9]+).*/\1/p' "${output_file}" | tail -1)"
    local skipped
    skipped="$(sed -nE 's/^[[:space:]]*skipped:[[:space:]]*([0-9]+).*/\1/p' "${output_file}" | tail -1)"
    rm -f -- "${output_file}"
    if [[ ${test_status} -ne 0 || "${test_count}" != 1 || "${skipped}" != 0 ]]; then
        echo "break-pane transition failed for tmux ${version} on ${framework}" >&2
        return 1
    fi
}

for version in "${REQUIRED_VERSIONS[@]}"; do
    run_version "${version}" false
done
if [[ ${include_master} -eq 1 ]]; then
    run_version master true
fi

if [[ ${transition_proof} -eq 1 ]]; then
    transition_successor_output="$("${SCRIPT_DIRECTORY}/build-version.sh" 3.7a)"
    transition_successor_binary="$(sed -n 's/^binary=//p' <<<"${transition_successor_output}" | tail -1)"
    transition_successor_commit="$(sed -n 's/^commit=//p' <<<"${transition_successor_output}" | tail -1)"
    if [[ ! -x "${transition_successor_binary}" \
        || ! "${transition_successor_commit}" =~ ^[0-9a-f]{40}$ \
        || "$("${transition_successor_binary}" -V)" != "tmux 3.7a" ]]; then
        echo "invalid transition build-version output for 3.7a" >&2
        exit 1
    fi
    for framework in "${FRAMEWORKS[@]}"; do
        run_transition_one \
            "${TRANSITION_VERSION}" \
            "${framework}" \
            "${TRANSITION_TMUX_BINARY}" \
            "${TRANSITION_TMUX_SOURCE_COMMIT}"
        run_transition_one \
            3.7a \
            "${framework}" \
            "${transition_successor_binary}" \
            "${transition_successor_commit}"
    done
fi

if [[ -n "${candidate}" ]]; then
    include_master_json=false
    if [[ ${include_master} -eq 1 ]]; then
        include_master_json=true
    fi
    transition_proof_json=false
    if [[ ${transition_proof} -eq 1 ]]; then
        transition_proof_json=true
    fi
    capability_cohort_json=false
    if [[ -n "${capability_cohort}" ]]; then
        capability_cohort_json=true
    fi
    jq -n \
        --arg evaluatedCommit "${EVALUATED_COMMIT}" \
        --arg evaluatedCommitTree "${EVALUATED_COMMIT_TREE}" \
        --arg capabilityCohort "${capability_cohort}" \
        --arg platform "${PLATFORM_NAME}" \
        --arg sdkVersion "${SDK_VERSION}" \
        --arg sourceState "${SOURCE_STATE}" \
        --arg sourceTreeFingerprint "${SOURCE_TREE_FINGERPRINT}" \
        --arg transitionTmuxSourceCommit "${TRANSITION_TMUX_SOURCE_COMMIT}" \
        --argjson capabilityCohortPresent "${capability_cohort_json}" \
        --argjson includeMasterAdvisory "${include_master_json}" \
        --argjson transitionProof "${transition_proof_json}" \
        '({evaluatedCommit:$evaluatedCommit,frameworks:["net10.0","net8.0"],includeMasterAdvisory:$includeMasterAdvisory,platform:$platform,redactionProof:true,schemaVersion:1,sdkVersion:$sdkVersion,sourceState:$sourceState,sourceTreeFingerprint:$sourceTreeFingerprint,tmuxVersions:["3.2a","3.3a","3.4","3.5","3.6","3.7a","3.7b","3.7c"]} + (if $capabilityCohortPresent then {capabilityCohort:$capabilityCohort,evaluatedCommitTree:$evaluatedCommitTree} else {} end) + (if $transitionProof then {transitionTmuxSourceCommits:{"3.7":$transitionTmuxSourceCommit}} else {} end))' \
        > "${candidate}/environment.json"
    jq -n '{passed:true,rejected:["absolute-paths","emails","environment-values","executable-paths","hostnames","socket-names","temporary-directories","terminal-device-names","tokens","usernames"]}' \
        > "${candidate}/redaction-proof.json"
    mapfile -t producer_files < <(
        cd "${candidate}"
        # find rather than fd: this has to run on whatever a CI image ships,
        # and fd is not on one.
        find . -type f ! -name producer.json | sed 's|^\./||' | sort
    )
    jq -n \
        --arg evaluatedCommit "${EVALUATED_COMMIT}" \
        --arg sourceTreeFingerprint "${SOURCE_TREE_FINGERPRINT}" \
        --argjson files "$(printf '%s\n' "${producer_files[@]}" | jq -R . | jq -s .)" \
        '{evaluatedCommit:$evaluatedCommit,files:$files,producer:"matrix",schemaVersion:1,sourceTreeFingerprint:$sourceTreeFingerprint}' \
        > "${candidate}/producer.json"
    uv run python eng/evidence/validate.py --phase matrix "${candidate}"
    uv run python eng/evidence/assemble_bundle.py \
        --publish-candidate "${candidate}" \
        --output "${evidence_directory}" \
        --ownership-nonce "${ownership_nonce}"
    candidate=
    ownership_nonce=
fi
