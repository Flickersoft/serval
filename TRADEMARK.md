# Serval™ Trademark Guidelines

The Serval name, logos and brand assets are owned by Flickersoft LLC ("the Licensor"), which is
also the copyright holder of the Serval software — Copyright (C) 2026 Flickersoft LLC.

The code behind **Serval** is released under the **GNU Affero General Public License v3 (AGPLv3)**. While the AGPLv3 gives you broad rights to modify and distribute the software code, **it does not grant you rights to use the Serval trademark or brand assets**.

These guidelines explain what you can and cannot do with the Serval name, logos, and domain (`serval.video`).

**Status of this document.** Sections 1 and 2 below are *additional terms* under section 7 of the
AGPLv3, permitted by 7(c) — requiring that modified versions be marked in reasonable ways as
different from the original — and 7(e) — declining to grant rights under trademark law. They form
part of the license under which this Program is offered, and apply to the code, the user
interface, and any modified version made available over a network. Nothing here restricts the
rights the AGPLv3 grants over the code itself: you may always modify, run and redistribute
Serval, provided you do so under a different name.

---

## 1. Allowed Uses (No Permission Required)

You are welcome—and encouraged—to do the following without explicit permission:

* **Truthful References:** You may state that your product or service "runs on Serval," "works with Serval," or is "a plugin for Serval."
* **Personal & Unmodified Distribution:** You may distribute original, unmodified builds or installer scripts of the Serval software, provided you do not alter the code or UI to impersonate official builds.
* **Community Contributions:** You may create tutorials, videos, or blog posts about Serval (e.g., *"How to set up Serval on Debian"*).

---

## 2. Forbidden Uses (Requires Rebranding)

To prevent community confusion and maintain user trust, the following are **strictly prohibited**:

* **Modified Builds using the Name:** If you modify the codebase, fix a bug, or build a fork, **you must rebrand your project**. You cannot distribute a modified version under the name "Serval", "Serval NVR", or "Serval Video".
* **Misleading Domains & Services:** You may not register domain names, social media handles, or commercial service names that incorporate "Serval" in a way that implies official backing or affiliation (e.g., `serval-cloud.com` or `serval-official`).
* **Commercial Impersonation:** You may not sell pre-hosted instances of Serval using the Serval logos or name in a manner that suggests you are the official maintainer of the project.

---

## 3. How to Rebrand a Fork

If you decide to fork Serval under the AGPLv3, rebranding is simple:

1. Replace the name **Serval** with your own unique project name in the places a user or a
   package manager sees it: `Product`, `Authors` and `Company` in `Directory.Build.props`, `name`
   and `description` in `App/serval_app/pubspec.yaml`, documentation, and UI titles.
2. Remove any official Serval logos or branding assets from the dashboard and from
   `App/serval_app/assets/`.
3. Clearly state in your `README.md`: *"This project is a fork of Serval, but is independently maintained and not affiliated with the official Serval project."*

You do **not** need to rename internal source identifiers — namespaces such as
`Serval.CameraModule`, project files, assembly names or type names. Those are not user-facing
branding, and rewriting them is not required by these terms.

---

## Questions?

If you want to use the Serval brand in a way not covered here, please reach out via GitHub Issues or contact the maintainers directly.