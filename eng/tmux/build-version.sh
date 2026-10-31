#!/usr/bin/env bash
set -euo pipefail

readonly TMUX_REPOSITORY="${LIBTMUX_TMUX_REPOSITORY:-https://github.com/tmux/tmux.git}"
readonly SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly CSHARP_DIRECTORY="$(cd -- "${SCRIPT_DIRECTORY}/../.." && pwd)"
readonly ARTIFACT_DIRECTORY="${LIBTMUX_TMUX_ARTIFACT_DIRECTORY:-${CSHARP_DIRECTORY}/artifacts/tmux}"

if [[ -n "${LIBTMUX_BUILD_JOBS:-}" \
    && ! "${LIBTMUX_BUILD_JOBS}" =~ ^[1-9][0-9]*$ ]]; then
    echo "LIBTMUX_BUILD_JOBS must be a positive integer" >&2
    exit 2
fi

sha256_file() {
    local path="$1"
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "${path}" | awk '{print $1}'
        return
    fi
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "${path}" | awk '{print $1}'
        return
    fi
    echo "no SHA-256 tool is available" >&2
    return 1
}

usage() {
    echo "usage: build-version.sh <3.0a|3.1c|3.2a|3.3|3.3a|3.4|3.5|3.6|3.7|3.7a|3.7b|3.7c|3.8|master>" >&2
    echo "       build-version.sh --resolve <tag|branch|40-hex commit>" >&2
    echo "       build-version.sh --ref <tag|branch|40-hex commit>" >&2
    exit 2
}

# A release is built from the closed list below. Anything else, such as the
# tag, branch or commit a debugging run asks for, goes through --ref, which
# the release workflows never use.
MODE=release
case "${1:-}" in
    --resolve|--ref)
        [[ $# -eq 2 ]] || usage
        MODE="${1#--}"
        TARGET="$2"
        ;;
    *)
        [[ $# -eq 1 ]] || usage
        TARGET="$1"
        ;;
esac
readonly MODE TARGET

validate_ref() {
    local ref="$1"
    if [[ ! "${ref}" =~ ^[A-Za-z0-9._/-]{1,64}$ \
        || "${ref}" == -* || "${ref}" == *..* || "${ref}" == */ || "${ref}" == *.lock ]]; then
        echo "not a usable tmux ref: ${ref}" >&2
        return 1
    fi
    if [[ "${ref}" =~ ^[0-9a-f]{7,39}$ ]]; then
        echo "abbreviated commit: give the full 40-hex SHA for ${ref}" >&2
        return 1
    fi
}

# Name the commit a ref points at. A tag is peeled to its commit, and a tag
# wins over a branch of the same name, as it does for a clone.
resolve_commit() {
    local ref="$1"
    if [[ "${ref}" =~ ^[0-9a-f]{40}$ ]]; then
        printf '%s\n' "${ref}"
        return 0
    fi
    local listing name commit
    listing="$(git ls-remote "${TMUX_REPOSITORY}" \
        "refs/tags/${ref}^{}" "refs/tags/${ref}" "refs/heads/${ref}")" || return 1
    for name in "refs/tags/${ref}^{}" "refs/tags/${ref}" "refs/heads/${ref}"; do
        commit="$(awk -v name="${name}" '$2 == name {print $1; exit}' <<<"${listing}")"
        if [[ "${commit}" =~ ^[0-9a-f]{40}$ ]]; then
            printf '%s\n' "${commit}"
            return 0
        fi
    done
    echo "${ref} is not a tag, a branch or a full commit of ${TMUX_REPOSITORY}" >&2
    return 1
}

if [[ "${MODE}" == resolve ]]; then
    validate_ref "${TARGET}"
    requested_commit="$(resolve_commit "${TARGET}")"
    printf 'commit=%s\n' "${requested_commit}"
    exit 0
fi

if [[ "${MODE}" == ref ]]; then
    validate_ref "${TARGET}"
    # Assigned before readonly so that a failed resolution stops the script.
    REQUESTED_COMMIT="$(resolve_commit "${TARGET}")"
    readonly REQUESTED_COMMIT
    readonly VERSION="ref-${REQUESTED_COMMIT}"
else
    readonly VERSION="${TARGET}"
    case "${VERSION}" in
        3.0a|3.1c|3.2a|3.3|3.3a|3.4|3.5|3.6|3.7|3.7a|3.7b|3.7c|3.8|master) ;;
        *)
            echo "unsupported tmux version: ${VERSION}" >&2
            exit 2
            ;;
    esac
fi

readonly SOURCE_DIRECTORY="${ARTIFACT_DIRECTORY}/sources/${VERSION}"
readonly INSTALL_DIRECTORY="${ARTIFACT_DIRECTORY}/installs/${VERSION}"
readonly BINARY="${INSTALL_DIRECTORY}/bin/tmux"
readonly COMMIT_FILE="${INSTALL_DIRECTORY}/source-commit"
readonly METADATA_FILE="${INSTALL_DIRECTORY}/cache-metadata.json"

cache_valid() {
    [[ -x "${BINARY}" && -f "${COMMIT_FILE}" && -f "${METADATA_FILE}" ]] || return 1
    [[ -d "${SOURCE_DIRECTORY}/.git" ]] || return 1
    local source_commit
    source_commit="$(git -C "${SOURCE_DIRECTORY}" rev-parse HEAD 2>/dev/null)" || return 1
    [[ "${source_commit}" =~ ^[0-9a-f]{40}$ ]] || return 1
    [[ "$(<"${COMMIT_FILE}")" == "${source_commit}" ]] || return 1
    if [[ "${MODE}" == ref ]]; then
        [[ "${source_commit}" == "${REQUESTED_COMMIT}" ]] || return 1
    elif [[ "${VERSION}" != master ]]; then
        [[ "$(git -C "${SOURCE_DIRECTORY}" describe --tags --exact-match HEAD 2>/dev/null)" == "${VERSION}" ]] || return 1
    fi
    local digest
    digest="$(sha256_file "${BINARY}")" || return 1
    local binary_version
    binary_version="$(${BINARY} -V 2>/dev/null | sed -n 's/^tmux //p')" || return 1
    [[ -n "${binary_version}" ]] || return 1
    jq -e \
        --arg binarySha256 "${digest}" \
        --arg binaryVersion "${binary_version}" \
        --arg sourceCommit "${source_commit}" \
        --arg sourceRef "${TARGET}" \
        --arg version "${VERSION}" \
        'keys == ["binarySha256","binaryVersion","schemaVersion","sourceCommit","sourceRef","version"]
         and .schemaVersion == 1
         and .binarySha256 == $binarySha256
         and .binaryVersion == $binaryVersion
         and .sourceCommit == $sourceCommit
         and .sourceRef == $sourceRef
         and .version == $version' \
        "${METADATA_FILE}" >/dev/null || return 1
    [[ "${MODE}" == ref || "${VERSION}" == master || "${binary_version}" == "${VERSION}" ]]
}

# A build from a ref has no version to match, so its output says what it is.
print_version() {
    if [[ "${MODE}" == ref ]]; then
        printf 'version=%s\n' "$("${BINARY}" -V | sed -n 's/^tmux //p')"
    fi
}

if cache_valid; then
    printf 'binary=%s\n' "${BINARY}"
    printf 'commit=%s\n' "$(<"${COMMIT_FILE}")"
    print_version
    exit 0
fi
if [[ -e "${BINARY}" || -e "${COMMIT_FILE}" || -e "${METADATA_FILE}" ]]; then
    echo "cache validation failed; rebuilding ${VERSION}" >&2
fi

mkdir -p "${ARTIFACT_DIRECTORY}/sources" "${ARTIFACT_DIRECTORY}/installs"
if [[ ! -d "${SOURCE_DIRECTORY}/.git" ]]; then
    if [[ -e "${SOURCE_DIRECTORY}" ]]; then
        echo "refusing non-Git source directory: ${SOURCE_DIRECTORY}" >&2
        exit 1
    fi
    if [[ "${MODE}" == ref ]]; then
        git init --quiet "${SOURCE_DIRECTORY}"
    elif [[ "${VERSION}" == master ]]; then
        git clone --depth 1 --branch master "${TMUX_REPOSITORY}" "${SOURCE_DIRECTORY}"
    else
        git clone --depth 1 --branch "${VERSION}" "${TMUX_REPOSITORY}" "${SOURCE_DIRECTORY}"
    fi
fi
if [[ "${MODE}" == ref \
    && "$(git -C "${SOURCE_DIRECTORY}" rev-parse --verify --quiet HEAD 2>/dev/null || true)" != "${REQUESTED_COMMIT}" ]]; then
    git -C "${SOURCE_DIRECTORY}" fetch --quiet --depth 1 "${TMUX_REPOSITORY}" "${REQUESTED_COMMIT}"
    git -C "${SOURCE_DIRECTORY}" checkout --quiet --detach FETCH_HEAD
fi

readonly RESOLVED_COMMIT="$(git -C "${SOURCE_DIRECTORY}" rev-parse HEAD)"
if [[ ! "${RESOLVED_COMMIT}" =~ ^[0-9a-f]{40}$ ]]; then
    echo "tmux source did not resolve to a full commit" >&2
    exit 1
fi
if [[ "${MODE}" == ref ]]; then
    if [[ "${RESOLVED_COMMIT}" != "${REQUESTED_COMMIT}" ]]; then
        echo "tmux source is at ${RESOLVED_COMMIT}, not ${REQUESTED_COMMIT}" >&2
        exit 1
    fi
elif [[ "${VERSION}" != master ]]; then
    readonly RESOLVED_TAG="$(git -C "${SOURCE_DIRECTORY}" describe --tags --exact-match HEAD)"
    if [[ "${RESOLVED_TAG}" != "${VERSION}" ]]; then
        echo "tmux source tag mismatch: ${RESOLVED_TAG}" >&2
        exit 1
    fi
fi

readonly BUILD_JOBS="${LIBTMUX_BUILD_JOBS:-$(getconf _NPROCESSORS_ONLN)}"
if [[ ! "${BUILD_JOBS}" =~ ^[1-9][0-9]*$ ]]; then
    echo "detected build worker count must be a positive integer" >&2
    exit 1
fi

(
    cd "${SOURCE_DIRECTORY}"
    sh autogen.sh
    ./configure --prefix="${INSTALL_DIRECTORY}"
    make -j"${BUILD_JOBS}"
    make install
) >&2

if [[ ! -x "${BINARY}" ]]; then
    echo "tmux build did not produce ${BINARY}" >&2
    exit 1
fi
commit_candidate="$(mktemp "${INSTALL_DIRECTORY}/.source-commit.XXXXXX")"
printf '%s\n' "${RESOLVED_COMMIT}" > "${commit_candidate}"
mv -- "${commit_candidate}" "${COMMIT_FILE}"
readonly BINARY_DIGEST="$(sha256_file "${BINARY}")"
readonly BINARY_VERSION="$(${BINARY} -V | sed -n 's/^tmux //p')"
if [[ -z "${BINARY_VERSION}" \
    || ("${MODE}" == release && "${VERSION}" != master && "${BINARY_VERSION}" != "${VERSION}") ]]; then
    echo "tmux binary version mismatch: ${BINARY_VERSION}" >&2
    exit 1
fi
metadata_candidate="$(mktemp "${INSTALL_DIRECTORY}/.cache-metadata.XXXXXX")"
jq -n \
    --arg binarySha256 "${BINARY_DIGEST}" \
    --arg binaryVersion "${BINARY_VERSION}" \
    --arg sourceCommit "${RESOLVED_COMMIT}" \
    --arg sourceRef "${TARGET}" \
    --arg version "${VERSION}" \
    '{binarySha256:$binarySha256,binaryVersion:$binaryVersion,schemaVersion:1,sourceCommit:$sourceCommit,sourceRef:$sourceRef,version:$version}' \
    > "${metadata_candidate}"
mv -- "${metadata_candidate}" "${METADATA_FILE}"
printf 'binary=%s\n' "${BINARY}"
printf 'commit=%s\n' "${RESOLVED_COMMIT}"
print_version
