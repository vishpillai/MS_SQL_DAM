# Changelog

All notable changes to SQL DAM are documented here. This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Publisher contact details for Vishal Pillai across the app metadata, installers, and documentation.

### Fixed

- Persist monitored-feed cursors only after the captured event batch is written.
- Convert activity date filters using the browser's local time zone.
- Collect Windows OIDC client settings in the MSI wizard and protect the client secret with machine-scoped DPAPI.
- Allow local-only loopback installations without OIDC while keeping remote access and the host-agent API authenticated.

## [1.0.0] - 2026-10-19

This is the initial source repository baseline for the existing `1.0.0.0` Windows MSI package.

### Added

- Windows and Linux SQL DAM web service with a browser dashboard and Windows desktop client.
- SQL Server Extended Events and SQL Server Audit feed collection, activity search, alert rules, and alert triage.
- OIDC authentication, local DAM role assignments, organization settings, retention, and administrative audit trail.
- Optional SQL Server event storage, SMTP alert delivery, and authenticated host-agent ingestion API.
- Windows MSI and Linux service packaging workflows.