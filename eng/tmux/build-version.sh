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

if [[ $# -ne 1 ]]; then
    echo "usage: build-version.sh <3.2a|3.3a|3.4|3.5|3.6|3.7|3.7a|3.7b|3.7c|master>" >&2
    exit 2
fi

readonly VERSION="$1"
case "${VERSION}" in
    3.2a|3.3a|3.4|3.5|3.6|3.7|3.7a|3.7b|3.7c|master) ;;
    *)
        echo "unsupported tmux version: ${VERSION}" >&2
        exit 2
        ;;
esac

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
    if [[ "${VERSION}" != master ]]; then
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
        --arg sourceRef "${VERSION}" \
        --arg version "${VERSION}" \
        'keys == ["binarySha256","binaryVersion","schemaVersion","sourceCommit","sourceRef","version"]
         and .schemaVersion == 1
         and .binarySha256 == $binarySha256
         and .binaryVersion == $binaryVersion
         and .sourceCommit == $sourceCommit
         and .sourceRef == $sourceRef
         and .version == $version' \
        "${METADATA_FILE}" >/dev/null || return 1
    [[ "${VERSION}" == master || "${binary_version}" == "${VERSION}" ]]
}

if cache_valid; then
    printf 'binary=%s\n' "${BINARY}"
    printf 'commit=%s\n' "$(<"${COMMIT_FILE}")"
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
    if [[ "${VERSION}" == master ]]; then
        git clone --depth 1 --branch master "${TMUX_REPOSITORY}" "${SOURCE_DIRECTORY}"
    else
        git clone --depth 1 --branch "${VERSION}" "${TMUX_REPOSITORY}" "${SOURCE_DIRECTORY}"
    fi
fi

readonly RESOLVED_COMMIT="$(git -C "${SOURCE_DIRECTORY}" rev-parse HEAD)"
if [[ ! "${RESOLVED_COMMIT}" =~ ^[0-9a-f]{40}$ ]]; then
    echo "tmux source did not resolve to a full commit" >&2
    exit 1
fi
if [[ "${VERSION}" != master ]]; then
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
if [[ -z "${BINARY_VERSION}" || ("${VERSION}" != master && "${BINARY_VERSION}" != "${VERSION}") ]]; then
    echo "tmux binary version mismatch: ${BINARY_VERSION}" >&2
    exit 1
fi
metadata_candidate="$(mktemp "${INSTALL_DIRECTORY}/.cache-metadata.XXXXXX")"
jq -n \
    --arg binarySha256 "${BINARY_DIGEST}" \
    --arg binaryVersion "${BINARY_VERSION}" \
    --arg sourceCommit "${RESOLVED_COMMIT}" \
    --arg sourceRef "${VERSION}" \
    --arg version "${VERSION}" \
    '{binarySha256:$binarySha256,binaryVersion:$binaryVersion,schemaVersion:1,sourceCommit:$sourceCommit,sourceRef:$sourceRef,version:$version}' \
    > "${metadata_candidate}"
mv -- "${metadata_candidate}" "${METADATA_FILE}"
printf 'binary=%s\n' "${BINARY}"
printf 'commit=%s\n' "${RESOLVED_COMMIT}"
