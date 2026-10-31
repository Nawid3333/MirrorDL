# Publishing to the Microsoft Store

Once this is set up, every release goes to the Microsoft Store by itself: `release.yml` builds the
MSIX package and submits it, next to the zips on GitHub. Store users then get updates from the Store.

You set it up once. Microsoft requires two things by hand: reserving the app's name, and the very
first submission. Everything after that comes from GitHub.

You need the Partner Center developer account; nothing has to be installed.

## 1. Reserve the name

1. [Partner Center](https://partner.microsoft.com/dashboard/apps-and-games/overview) > **Apps and games** >
   **New product** > **MSIX or PWA app**, and reserve **MirrorDL**.
2. Open the app > **Product management** > **Product identity**. Copy three values into the GitHub
   repository as **variables** (Settings > Secrets and variables > Actions > **Variables** tab >
   New repository variable). None of them is secret. From now on, every release also builds the
   Store package, but doesn't submit it yet:

   | Variable | Value on the Product identity page |
   |---|---|
   | `STORE_IDENTITY_NAME` | Package/Identity/Name |
   | `STORE_PUBLISHER` | Package/Identity/Publisher, like `CN=1A2B3C4D-...` |
   | `STORE_PUBLISHER_DISPLAY_NAME` | Package/Properties/PublisherDisplayName |

   Note the **Store ID** too (like `9N1234567890`); it comes in at the very end.

## 2. The first submission, by hand

1. GitHub > **Actions** > **Release** > **Run workflow**, with the latest release's version (like `1.1.0`).
   The run builds the Store package with the identity from step 1. Download its **release-files**
   artifact; it contains `MirrorDL-x64.msix`.
2. Partner Center > the app > **Start your submission**, and fill in (every text and answer, ready to
   copy: [store-listing.md](store-listing.md)):
   - **Pricing and availability:** free.
   - **Properties:** category Utilities & tools; privacy policy URL
     `https://github.com/Nawid3333/MirrorDL/blob/main/PRIVACY.md`.
   - **Age ratings:** the questionnaire. MirrorDL has no user content or chat.
   - **Packages:** upload `MirrorDL-x64.msix`.
   - **Store listings:** description, screenshots and the 300 x 300 icon (`assets/store/`).
   - Partner Center asks why the package uses `runFullTrust`. An answer: *"MirrorDL is a desktop
     download manager. It saves downloads in folders the user chooses and continues unfinished
     downloads of other programs (.part files of Firefox, aria2), which needs normal access to files."*
3. **Submit to the Store.** Certification takes from hours to a few days. The automation works from
   the moment the app is live.

## 3. Let GitHub submit

GitHub signs in to Partner Center with a Microsoft Entra application: a login for programs, which
you can delete any time.

1. Partner Center > gear icon > **Account settings** > **Tenants**: associate a Microsoft Entra ID
   tenant, or create a new one there (free).
2. **Account settings** > **User management** > **Microsoft Entra applications** >
   **Create Microsoft Entra application** (or add an existing one), with the **Manager** role.
3. Click the application: copy **Tenant ID** and **Client ID**. Click **Add new key** and copy the
   **Key** at once; Partner Center shows it only this one time.
4. **Account settings** > **Legal info** > **Developer**: copy the **Seller ID**.
5. Put the four into the GitHub repository as **secrets** (Settings > Secrets and variables >
   Actions > **Secrets** tab > New repository secret). Type them in there yourself and share them
   with no one:

   | Secret | Value |
   |---|---|
   | `AZURE_AD_TENANT_ID` | Tenant ID |
   | `AZURE_AD_APPLICATION_CLIENT_ID` | Client ID |
   | `AZURE_AD_APPLICATION_SECRET` | the Key |
   | `SELLER_ID` | Seller ID |

## 4. Switch it on

Once the first submission is **live** in the Store and the four secrets are in, add the last
variable, `STORE_PRODUCT_ID`, with the Store ID from step 1.

That's all. From the next release on, the **Auto release** run ends with a **Microsoft Store** job
that submits the same version, after the GitHub release is out. Partner Center shows the
certification. A Store problem never holds up the GitHub release; fix it and re-run that job.

## Good to know

- A submission the workflow created must be changed only by the workflow. Edited in Partner Center,
  it can get stuck; then delete it there, and re-run the release's **Microsoft Store**
  job.
- The Store version number is the release's, with `.0` added: `v1.2.3` becomes `1.2.3.0`. It has to
  grow with every submission, which the releases do by themselves.
- The key expires (Partner Center shows when). Before it does, add a new key and replace the
  `AZURE_AD_APPLICATION_SECRET` secret.
- The Store version doesn't look for updates on GitHub; the Store updates it. The GitHub version
  shows a notice in its window when a newer release exists.
- A release you publish by hand as a **pre-release** stays on GitHub: the Store has no pre-releases,
  so it would go out to everyone.
