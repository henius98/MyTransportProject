# Firebase & Google Cloud Setup Guide

> Step-by-step setup for **MyTransportProject** — a Blazor WebAssembly (C#/.NET) transport app deployed to GitHub Pages.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Create a Google Cloud Project](#2-create-a-google-cloud-project)
3. [Set Up Firebase](#3-set-up-firebase)
4. [Install Firebase CLI](#4-install-firebase-cli)
5. [Initialize Firebase in Your Project](#5-initialize-firebase-in-your-project)
6. [Configure the Blazor App](#6-configure-the-blazor-app)
7. [Enable Google Cloud APIs](#7-enable-google-cloud-apis)
8. [Set Up Firebase Authentication](#8-set-up-firebase-authentication)
9. [Set Up Cloud Firestore](#9-set-up-cloud-firestore)
10. [Set Up Cloud Functions](#10-set-up-cloud-functions)
11. [Set Up Push Notifications (FCM)](#11-set-up-push-notifications-fcm)
12. [Set Up Google OAuth (Calendar & Tasks)](#12-set-up-google-oauth-calendar--tasks)
13. [Set Up Google Maps](#13-set-up-google-maps)
14. [Firebase Emulators (Local Dev)](#14-firebase-emulators-local-dev)
15. [CI/CD Configuration](#15-cicd-configuration)
16. [Verify Everything Works](#16-verify-everything-works)

---

## 1. Prerequisites

| Tool           | Version | Install                                            |
| -------------- | ------- | -------------------------------------------------- |
| .NET SDK       | 10.0+   | [dot.net/download](https://dot.net/download)       |
| Node.js        | 22+     | [nodejs.org](https://nodejs.org)                   |
| Java JDK       | 11+     | `sudo apt install default-jdk` (for emulators)     |
| Google account | —       | [accounts.google.com](https://accounts.google.com) |

Verify:

```bash
dotnet --version
node --version
java --version
```

---

## 2. Create a Google Cloud Project

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Click **Select a project** → **New Project**
3. Name it (e.g. `mytransportapp`)
4. Note the **Project ID** — you'll need it everywhere

### Enable Billing

1. Sidebar → **Billing** → **Link a billing account**
2. Add a payment method

> [!IMPORTANT]
> Billing is required for Cloud Functions, Google Maps APIs, and extended quotas.

### Install Google Cloud CLI (optional)

```bash
curl https://sdk.cloud.google.com | bash
gcloud init
gcloud auth login
gcloud config set project YOUR_PROJECT_ID
```

---

## 3. Set Up Firebase

1. Go to [Firebase Console](https://console.firebase.google.com/)
2. Click **Add project** → select the Google Cloud project you just created
3. Enable or disable Google Analytics (optional)
4. Click **Create project**

### Register the Web App

1. In Firebase Console → **Project Settings** → **Your apps**
2. Click the **Web** icon (`</>`)
3. App nickname: `MyTransportApp`
4. **Do NOT** check "Also set up Firebase Hosting" (you're deploying to GitHub Pages)
5. Click **Register app**
6. Copy the config object — you'll need these values:

```json
{
  "apiKey": "AIza...",
  "authDomain": "your-project.firebaseapp.com",
  "projectId": "your-project-id",
  "storageBucket": "your-project.firebasestorage.app",
  "messagingSenderId": "123456789",
  "appId": "1:123456789:web:abcdef",
  "measurementId": "G-XXXXXXX"
}
```

---

## 4. Install Firebase CLI

**Option A — Standalone (recommended):**

```bash
curl -sL https://firebase.tools | bash
```

**Option B — npm:**

```bash
npm install -g firebase-tools
```

Log in:

```bash
firebase login
```

Verify:

```bash
firebase --version
```

---

## 5. Initialize Firebase in Your Project

From the project root (`~/src/MyTransportProject`):

```bash
firebase init
```

Select these services when prompted:

- ✅ **Firestore** — rules and indexes
- ✅ **Functions** — Cloud Functions (Node.js)
- ✅ **Emulators** — for local development

When asked:

- **Firestore Rules file:** `firestore.rules` (already exists)
- **Firestore Indexes file:** `firestore.indexes.json`
- **Functions language:** JavaScript
- **Functions directory:** `functions`
- **Use ESLint:** No
- **Install dependencies:** Yes

### Bind to Your Firebase Project

Create `.firebaserc` in the project root (or let `firebase use --add` create it):

```bash
firebase use --add
```

Select your project and give it an alias (e.g. `default`).

This creates `.firebaserc`:

```json
{
  "projects": {
    "default": "mytransportapp-f17d5"
  }
}
```

---

## 6. Configure the Blazor App

This app uses Firebase via **JavaScript interop** — the Firebase JS SDK is loaded in the browser and called from C# via `IJSRuntime`.

### 6.1 Development Config

Edit [`MyTransportAppWASM/wwwroot/appsettings.Development.json`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/appsettings.Development.json) with your Firebase values:

```json
{
  "Firebase": {
    "apiKey": "YOUR_FIREBASE_API_KEY",
    "authDomain": "your-project.firebaseapp.com",
    "projectId": "your-project-id",
    "storageBucket": "your-project.firebasestorage.app",
    "messagingSenderId": "123456789",
    "appId": "1:123456789:web:abcdef",
    "measurementId": "G-XXXXXXX",
    "googleClientId": "YOUR_GOOGLE_OAUTH_CLIENT_ID"
  },
  "GoogleMaps": {
    "ApiKey": "YOUR_GOOGLE_MAPS_API_KEY"
  }
}
```

### 6.2 Production Config (Template)

The production template at [`MyTransportAppWASM/wwwroot/appsettings.json`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/appsettings.json) keeps placeholder/empty values. The CI/CD pipeline injects real values at build time via the [`scripts/configure-firebase.mjs`](file:///var/home/henius/src/MyTransportProject/scripts/configure-firebase.mjs) script (see [Step 15](#15-cicd-configuration)).

> [!CAUTION]
> Never commit real API keys to `appsettings.json`. Only `appsettings.Development.json` should contain actual keys, and it should be in `.gitignore`.

---

## 7. Enable Google Cloud APIs

Go to [APIs & Services → Library](https://console.cloud.google.com/apis/library) and enable:

| API                          | Purpose               |
| ---------------------------- | --------------------- |
| Cloud Firestore API          | Database              |
| Cloud Functions API          | Backend logic         |
| Firebase Cloud Messaging API | Push notifications    |
| Identity Toolkit API         | Authentication        |
| Maps JavaScript API          | Interactive maps      |
| Directions API               | Route calculation     |
| Places API                   | Location search       |
| Geocoding API                | Address ↔ coordinates |
| Google Calendar API          | Calendar integration  |
| Google Tasks API             | Tasks integration     |

Or via CLI:

```bash
gcloud services enable firestore.googleapis.com
gcloud services enable cloudfunctions.googleapis.com
gcloud services enable fcm.googleapis.com
gcloud services enable identitytoolkit.googleapis.com
gcloud services enable maps-backend.googleapis.com
gcloud services enable directions-backend.googleapis.com
gcloud services enable places-backend.googleapis.com
gcloud services enable geocoding-backend.googleapis.com
gcloud services enable calendar-json.googleapis.com
gcloud services enable tasks.googleapis.com
```

---

## 8. Set Up Firebase Authentication

### 8.1 Enable Google Sign-In Provider

1. Firebase Console → **Authentication** → **Sign-in method**
2. Click **Google** → **Enable**
3. Set your support email
4. Click **Save**

### 8.2 Configure Authorized Domains

1. Firebase Console → **Authentication** → **Settings** → **Authorized domains**
2. Add your GitHub Pages domain: `yourusername.github.io`
3. Add `localhost` (should already be there)

### 8.3 How It Works in the App

The app uses Firebase Auth via JS interop:

- [`wwwroot/js/firebase-auth.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/firebase-auth.js) — JS module for Google sign-in (popup + One Tap)
- [`Services/FirebaseAuthenticationStateProvider.cs`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/Services/FirebaseAuthenticationStateProvider.cs) — Blazor `AuthenticationStateProvider` that calls the JS module

No additional code changes needed — just ensure the Firebase config values are correct.

---

## 9. Set Up Cloud Firestore

### 9.1 Create the Database

1. Firebase Console → **Firestore Database** → **Create database**
2. Choose **Production mode**
3. Select a region close to your users (e.g. `asia-southeast1` for Malaysia)

### 9.2 Deploy Security Rules

The project has comprehensive rules in [`firestore.rules`](file:///var/home/henius/src/MyTransportProject/firestore.rules) covering:

| Collection                           | Access                                |
| ------------------------------------ | ------------------------------------- |
| `users/{uid}/data/settings`          | Owner read/write with validation      |
| `users/{uid}/taskExtensions/{id}`    | Owner read/write with validation      |
| `pushDevices/{deviceId}`             | Backend only (deny all client access) |
| `users/{uid}/googleConnections/{id}` | Backend only (deny all client access) |

Deploy rules:

```bash
firebase deploy --only firestore:rules
```

### 9.3 How It Works in the App

- [`wwwroot/js/firebase-firestore.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/firebase-firestore.js) — JS module for Firestore reads/writes
- [`wwwroot/js/firebase-task-extensions.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/firebase-task-extensions.js) — JS module for task extension CRUD
- [`Services/UserSettingsService.cs`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/Services/UserSettingsService.cs) — C# service that calls Firestore via JS interop

---

## 10. Set Up Cloud Functions

### 10.1 Install Dependencies

```bash
cd functions
pnpm install
```

### 10.2 Set Function Secrets

The Cloud Functions need secrets for Google OAuth:

```bash
firebase functions:secrets:set GOOGLE_CLIENT_SECRET
# Paste your Google OAuth client secret when prompted
```

### 10.3 Deploy Functions

```bash
firebase deploy --only functions
```

This deploys three functions:

| Function           | Purpose                                            |
| ------------------ | -------------------------------------------------- |
| `googleConnection` | Google OAuth code exchange for Calendar/Tasks      |
| `pushRegistration` | Push notification device registration              |
| `pushWebhook`      | Receives push notifications from external services |

### 10.4 Verify Deployment

```bash
firebase functions:list
```

> [!NOTE]
> Cloud Functions require the **Blaze (pay-as-you-go)** billing plan.

---

## 11. Set Up Push Notifications (FCM)

### 11.1 Generate VAPID Key

1. Firebase Console → **Project Settings** → **Cloud Messaging**
2. Under **Web configuration**, click **Generate key pair**
3. Copy the key and add it to your Firebase config as `webPushVapidKey`

### 11.2 How It Works in the App

- [`wwwroot/firebase-messaging-sw.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/firebase-messaging-sw.js) — Service worker for background push messages
- [`wwwroot/js/push-notifications.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/push-notifications.js) — JS module for requesting permission and subscribing

### 11.3 Update the Service Worker

Edit `firebase-messaging-sw.js` and update the Firebase config to match your project:

```javascript
const firebaseConfig = {
  apiKey: "YOUR_API_KEY",
  authDomain: "your-project.firebaseapp.com",
  projectId: "your-project-id",
  storageBucket: "your-project.firebasestorage.app",
  messagingSenderId: "123456789",
  appId: "1:123456789:web:abcdef",
};
```

---

## 12. Set Up Google OAuth (Calendar & Tasks)

The app integrates with Google Calendar and Tasks APIs via a backend Cloud Function.

### 12.1 Create OAuth Client ID

1. [Google Cloud Console → Credentials](https://console.cloud.google.com/apis/credentials)
2. Click **Create Credentials** → **OAuth client ID**
3. Application type: **Web application**
4. Name: `MyTransportApp`
5. **Authorized JavaScript origins:**
   - `http://localhost:5001` (local dev)
   - `https://yourusername.github.io` (production)
6. **Authorized redirect URIs:**
   - `http://localhost:5001` (local dev)
   - `https://yourusername.github.io` (production)
7. Click **Create**
8. Copy the **Client ID** → add as `googleClientId` in your Firebase config
9. Copy the **Client Secret** → set as Cloud Function secret (Step 10.2)

### 12.2 Configure OAuth Consent Screen

1. [Google Cloud Console → OAuth consent screen](https://console.cloud.google.com/apis/credentials/consent)
2. User type: **External**
3. Fill in app name, support email, developer email
4. Add scopes:
   - `calendar.calendarlist.readonly`
   - `calendar.events`
   - `tasks`
5. Add test users (while in testing mode)
6. Submit for verification when ready for production

---

## 13. Set Up Google Maps

### 13.1 Create API Key

1. [Google Cloud Console → Credentials](https://console.cloud.google.com/apis/credentials)
2. Click **Create Credentials** → **API Key**
3. Click **Restrict Key**:
   - **Application restrictions:** HTTP referrers
   - Add: `yourusername.github.io/*` and `localhost:*`
   - **API restrictions:** Restrict to Maps JavaScript API, Directions API, Places API, Geocoding API
4. Copy the key → add as `GoogleMaps.ApiKey` in your config

### 13.2 How It Works in the App

- [`wwwroot/js/googleMap.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/googleMap.js) — JS module for map rendering, markers, directions
- [`wwwroot/js/geolocation.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/geolocation.js) — Browser geolocation API wrapper
- [`wwwroot/js/locationPicker.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/locationPicker.js) — Location picker component

> [!WARNING]
> Restrict your API keys to prevent unauthorized usage. Unrestricted keys can lead to unexpected billing charges.

---

## 14. Firebase Emulators (Local Dev)

### 14.1 Initialize Emulators

```bash
firebase init emulators
```

The current config in [`firebase.json`](file:///var/home/henius/src/MyTransportProject/firebase.json) sets up:

| Emulator  | Port |
| --------- | ---- |
| Firestore | 8080 |

### 14.2 Start Emulators

```bash
firebase emulators:start
```

### 14.3 Connect the App to Emulators

In your JS interop modules, point Firestore calls to `localhost:8080` when in development mode. The app's JS modules in `wwwroot/js/` can be configured to use the emulator endpoint.

### 14.4 Persist Emulator Data

```bash
firebase emulators:start --export-on-exit=./emulator-data --import=./emulator-data
```

---

## 15. CI/CD Configuration

The project deploys via GitHub Actions to GitHub Pages.

### 15.1 Set Repository Secrets/Variables

In your GitHub repo → **Settings** → **Secrets and variables** → **Actions**:

**Variables (non-secret):**

| Variable               | Value                                 |
| ---------------------- | ------------------------------------- |
| `FIREBASE_CONFIG_JSON` | Full Firebase config JSON (see below) |

```json
{
  "apiKey": "AIza...",
  "authDomain": "your-project.firebaseapp.com",
  "projectId": "your-project-id",
  "storageBucket": "your-project.firebasestorage.app",
  "messagingSenderId": "123456789",
  "appId": "1:123456789:web:abcdef",
  "measurementId": "G-XXXXXXX",
  "googleClientId": "123456789-abcdef.apps.googleusercontent.com",
  "webPushVapidKey": "BPxxxxxxx..."
}
```

**Secrets:**

| Secret                | Value             |
| --------------------- | ----------------- |
| `GOOGLE_MAPS_API_KEY` | Your Maps API key |

### 15.2 How Build-Time Config Injection Works

The script [`scripts/configure-firebase.mjs`](file:///var/home/henius/src/MyTransportProject/scripts/configure-firebase.mjs) runs during CI/CD:

1. Reads `wwwroot/appsettings.json`
2. Parses the `FIREBASE_CONFIG_JSON` environment variable
3. Validates required keys (`apiKey`, `authDomain`, `projectId`, `appId`)
4. Replaces the empty Firebase section with real values
5. Writes the updated file

The GitHub Actions workflow calls it like:

```bash
FIREBASE_CONFIG_JSON='${{ vars.FIREBASE_CONFIG_JSON }}' node scripts/configure-firebase.mjs
```

---

## 16. Verify Everything Works

### 16.1 Run the App Locally

```bash
dotnet run --project MyTransportAppWASM
```

Open `https://localhost:5001` (or the port shown in console).

### 16.2 Test Each Service

| Service       | How to Test                                                   |
| ------------- | ------------------------------------------------------------- |
| **Auth**      | Click sign-in → Google popup should appear                    |
| **Firestore** | Sign in → change theme/language → settings should persist     |
| **Functions** | Connect Google Calendar → tokens should exchange successfully |
| **Maps**      | Open the Map page → tiles should render with markers          |
| **Push**      | Allow notifications → token should register                   |

### 16.3 Deploy Firestore Rules & Functions

```bash
# Deploy rules
firebase deploy --only firestore:rules

# Deploy functions
firebase deploy --only functions

# Deploy everything
firebase deploy
```

### 16.4 Deploy the App

Push to your main branch — GitHub Actions will build and deploy to GitHub Pages automatically.

---

## Quick Reference

```bash
# Firebase CLI
firebase login                          # Log in
firebase projects:list                  # List projects
firebase use <alias>                    # Switch project
firebase deploy                         # Deploy all
firebase deploy --only firestore:rules  # Deploy rules only
firebase deploy --only functions        # Deploy functions only
firebase emulators:start                # Start emulators
firebase functions:secrets:set KEY      # Set a secret

# .NET / Blazor
dotnet run --project MyTransportAppWASM # Run locally
dotnet build                            # Build
dotnet test                             # Run tests
dotnet publish -c Release               # Publish

# Cloud Functions
cd functions && pnpm install            # Install deps
cd functions && pnpm test               # Run function tests

# Google Cloud
gcloud auth login                       # Log in
gcloud services enable <api>            # Enable API
gcloud services list --enabled          # List enabled APIs
```

---

## Key Files Reference

| File                                                                                                                                     | Purpose                                                    |
| ---------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| [`appsettings.json`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/appsettings.json)                         | Production config template (empty Firebase values)         |
| [`appsettings.Development.json`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/appsettings.Development.json) | Local dev config with real Firebase values                 |
| [`firebase.json`](file:///var/home/henius/src/MyTransportProject/firebase.json)                                                          | Firebase services config (Functions, Firestore, Emulators) |
| [`firestore.rules`](file:///var/home/henius/src/MyTransportProject/firestore.rules)                                                      | Firestore security rules                                   |
| [`firebase-messaging-sw.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/firebase-messaging-sw.js)         | FCM service worker for push notifications                  |
| [`functions/index.js`](file:///var/home/henius/src/MyTransportProject/functions/index.js)                                                | Cloud Functions entry point                                |
| [`functions/google-connections.js`](file:///var/home/henius/src/MyTransportProject/functions/google-connections.js)                      | Google OAuth token exchange function                       |
| [`functions/push-notifications.js`](file:///var/home/henius/src/MyTransportProject/functions/push-notifications.js)                      | Push registration & webhook functions                      |
| [`scripts/configure-firebase.mjs`](file:///var/home/henius/src/MyTransportProject/scripts/configure-firebase.mjs)                        | CI/CD build-time config injection                          |
| [`wwwroot/js/firebase-auth.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/firebase-auth.js)           | Firebase Auth JS interop module                            |
| [`wwwroot/js/firebase-firestore.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/firebase-firestore.js) | Firestore JS interop module                                |
| [`wwwroot/js/googleMap.js`](file:///var/home/henius/src/MyTransportProject/MyTransportAppWASM/wwwroot/js/googleMap.js)                   | Google Maps JS interop module                              |
