# The actionlint image that ci.yml's "Lint workflows" job runs. Nothing builds this file; ci.yml
# reads the FROM line. It is here and not in ci.yml because Dependabot updates the tag and digest
# of a Dockerfile's FROM line (the docker entry in .github/dependabot.yml), but not a docker://
# line in a workflow.
FROM rhysd/actionlint:1.7.12@sha256:b1934ee5f1c509618f2508e6eb47ee0d3520686341fec936f3b79331f9315667
