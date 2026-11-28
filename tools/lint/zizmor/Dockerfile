# The zizmor image that ci.yml's "Lint workflows" job runs; its settings are in .github/zizmor.yml.
# Nothing builds this file; ci.yml reads the FROM line. It is here and not in ci.yml because
# Dependabot updates the tag and digest of a Dockerfile's FROM line (the docker entry in
# .github/dependabot.yml), but not a docker:// line in a workflow.
FROM ghcr.io/zizmorcore/zizmor:1.33.1@sha256:ecd3ffe6158de1322e05a044ed43749bee12b1dccb7e1a7859bc6ac4b2a1c516
