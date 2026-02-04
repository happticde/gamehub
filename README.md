# GameHub Backend

A unified game library aggregator that ingests data from **Steam**, **PSN**, **GOG**, and **Epic Games** into a single queryable "Data Lake". It provides a REST API to access raw game ownership data, designed to be resilient and idempotent.

## 🚀 Features

* **Unified Ingestion:** Separate endpoints for PSN, Steam, GOG, and Epic.
* **Idempotent Storage:** Re-running import scripts updates existing records instead of creating duplicates.
* **Multi-Provider DB:** Configurable support for **SQLite** (default/dev) and **PostgreSQL** (production).
* **Raw Data Preservation:** Stores the full JSON response from providers, allowing for future schema evolution without re-fetching.
* **Dockerized:** Full DevContainer support and production-ready Dockerfile.


## 🛠️ Tech Stack

* **.NET 10** (ASP.NET Core Web API)
* **Entity Framework Core**
* **SQLite** / **PostgreSQL**
* **Python 3** (for import scripts)
* **Legendary** (for Epic Games auth)

***

## ⚡ Quick Start (Development)

The easiest way to run the project is using **VS Code Dev Containers**. This requires **Docker Desktop** and **VS Code** with the *Dev Containers* extension.

1. **Open in VS Code:**
Open this folder in VS Code. Click **"Reopen in Container"** when prompted.
*(This installs .NET SDK, Python, Legendary, and DB tools automatically inside Docker.)*
2. **Run the API:**
Open the integrated terminal (Ctrl+`)` and run:

```bash
dotnet run
```

The API is now live at `http://localhost:5000`.
Swagger UI: `http://localhost:5000/swagger`
3. **Run Import Scripts:**
Open a *new* terminal split (still inside the container) to run Python scripts.

***

## 📥 Ingesting Data

Scripts are located in the project root. Ensure the Backend is running before executing these.

### 🎮 Steam

Requires an API Key and your 64-bit Steam ID.

* Get Key: https://steamcommunity.com/dev/apikey
* Get ID: https://steamdb.info/calculator/

```bash
python3 import_steam.py <YOUR_API_KEY> <YOUR_STEAM_ID>
```


### 🎮 PlayStation (PSN)

Requires an `npsso` token from your Sony account login.

* Guide to get token: [PSNAWP Docs](https://psnawp.readthedocs.io/en/latest/authentication/)

```bash
python3 import_psn.py <YOUR_NPSSO_TOKEN>
```


### 🎮 GOG

Requires the `gog-al` cookie value from a logged-in browser session.

1. Log into GOG.com
2. Open DevTools -> Application -> Cookies -> `gog-al`
```bash
python3 import_gog.py <YOUR_GOG_AL_COOKIE>
```


### 🎮 Epic Games

Uses **Legendary** (pre-installed in container).

1. **Authenticate (One-time):**

```bash
legendary auth
```

*(Follow the prompts to copy the SID from your browser)*
2. **Run Import:**

```bash
python3 import_epic.py
```


***

## 🐳 Running in Production (Docker)

To run the backend as a standalone service (without VS Code).

### 1. Build the Image

```bash
docker build -t gamehub-backend .
```


### 2. Run (SQLite)

Persist data to a local `data/` folder.

```bash
docker run -d \
  -p 8080:8080 \
  -v $(pwd)/data:/app/data \
  -e Database__Provider="Sqlite" \
  -e ConnectionStrings__Sqlite="Data Source=/app/data/gamehub.db" \
  gamehub-backend
```


### 3. Run (PostgreSQL)

Connect to an external Postgres database.

```bash
docker run -d \
  -p 8080:8080 \
  -e Database__Provider="Postgres" \
  -e ConnectionStrings__Postgres="Host=my-postgres;Database=gamehub;Username=user;Password=pass" \
  gamehub-backend
```


***

## 📂 Project Structure

* `Controllers/` - API Endpoints (`SteamIngestController`, `EpicIngestController`...)
* `Data/` - EF Core Context and Database Models (`SteamRawItem`, `PsnRawItem`...)
* `.devcontainer/` - VS Code environment configuration.
* `import_*.py` - Python ETL scripts.
* `gamehub.db` - Local SQLite database (created on first run).


## ⚠️ Notes

* **Schema Updates:** Since this is a single-user app, if you change the database schema, simply **delete `gamehub.db`** and restart the app. Then re-run your import scripts.
* **Swagger:** Available at `/swagger` or the root `/` URL.

