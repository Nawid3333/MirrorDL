# The zizmor image that ci.yml's "Lint workflows" job runs; its settings are in .github/zizmor.yml.
# Nothing builds this file; ci.yml reads the FROM line. It is here and not in ci.yml because
# Dependabot updates the tag and digest of a Dockerfile's FROM line (the docker entry in
# .github/dependabot.yml), but not a docker:// line in a workflow.
FROM ghcr.io/zizmorcore/zizmor:1.31.1@sha256:42d409dbb851d4c5d35a058cf042b828bea1e3f2884378c526eb0974780e18d9
