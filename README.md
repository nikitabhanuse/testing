# ResumeAI API

ASP.NET Core (.NET 10) API that reads a resume, finds live .NET/C# jobs in India, ranks them, explains each match with cited evidence, and tracks applications.
All AI runs locally on [Ollama](https://ollama.com); data is stored in PostgreSQL.

## What it does

| Area | Endpoints |
|---|---|
| **Resume intelligence**: upload PDF/DOCX/TXT, extract text and a structured profile (skills, years, titles, location), store it | `POST /api/resumes`, `GET /api/resumes/{id}` |
| **Job discovery**: import open jobs from employer career feeds (Greenhouse, SmartRecruiters) and remove duplicates | `GET /api/jobs`, `GET /api/jobs/{id}`, `POST /api/jobs/sync` |
| **Matching**: rank open jobs for a resume | `GET /api/resumes/{id}/matches` |
| **RAG explanation**: verified strengths and gaps, each citing a job `[J#]` or resume `[R#]` passage | `GET /api/resumes/{id}/matches/{jobId}/explain` |
| **Application workflow**: save a job, draft a tailored resume, approve it, track status | `/api/applications/...` |
| **Health**: API up, database reachable, row counts per table | `GET /health` |

### Notes on behaviour

- **Jobs are real.** They come from employers' own public career feeds, never from the AI. The companies searched are listed under `JobSources` in `appsettings.json`.
- **Match score (0-100):** skills 35 (keyword), meaning-based similarity 35 (embeddings), experience 20, location 10.
- **Explanations are checked in code.** The model extracts the job's required skills, then code keeps only terms that appear in the job text and checks each one against the resume. The model only writes the summary from those checked lists.
- **Approval comes before submission.** An application can only be marked `Submitted` after its tailored resume draft is approved. Editing a draft cancels its approval. The API never sends anything to employers: you apply via the job URL, then record it.
- Statuses: `Saved → Drafted → Approved → Submitted → Interviewing → Offer / Rejected`, or `Withdrawn` at any point before the end.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com) running on `http://localhost:11434`, with these models:
  ```
  ollama pull llama3.2
  ollama pull nomic-embed-text
  ```
- PostgreSQL 13+. No extensions are needed (pgvector isn't required).

## Setup

1. **Clone and restore**
   ```
   git clone https://github.com/nikitabhanuse/testing.git
   cd testing
   dotnet restore
   ```

2. **Set the database connection string.** It goes in user-secrets, never in `appsettings.json`:
   ```
   dotnet user-secrets set "ConnectionStrings:ResumeDb" "Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<password>" --project WebApplicationAI
   ```
   - The user needs permission to create a schema.
   - All tables go into a separate `resumeai` schema, so other tables in the same database are not touched.
   - If the database doesn't exist, it is created.

3. **Run**
   ```
   dotnet run --project WebApplicationAI --launch-profile http
   ```
   - The schema is created or upgraded automatically on startup (EF Core migrations).
   - API: `http://localhost:5275`
   - Swagger UI (Development only): `http://localhost:5275/swagger`

4. **Check it:** `GET http://localhost:5275/health` should return `"status": "Healthy"`.

User-secrets are only loaded in the `Development` environment. In other environments, set the connection string with the environment variable `ConnectionStrings__ResumeDb`.

## Configuration (`WebApplicationAI/appsettings.json`)

| Key | Purpose |
|---|---|
| `Ollama:ChatModel` | Model for profile extraction, explanations and drafts (default `llama3.2`) |
| `Ollama:EmbeddingModel` | Embedding model (default `nomic-embed-text`) |
| `JobSources` | Employer feeds: `{ "Ats": "greenhouse" \| "smartrecruiters", "Company": "<board id>" }` |
| `ConnectionStrings:ResumeDb` | Placeholder only; set the real value in user-secrets |

To find a company's board id:
- **Greenhouse:** `https://boards-api.greenhouse.io/v1/boards/<id>/jobs` returns JSON.
- **SmartRecruiters:** `https://api.smartrecruiters.com/v1/companies/<id>/postings` returns JSON.

## Testing with Postman

Import `postman/ResumeAI.postman_collection.json`. It has 22 requests with test scripts, in flow order:

1. **Health**: API and database
2. **Resumes**: in *Upload resume*, open the Body tab, choose a file for the `resume` field, and save the request
3. **Jobs**: sync, list, get
4. **Matching & RAG**: ranked matches, then an explanation (the tests check every cited passage really contains the skill)
5. **Applications**: save → submit is refused before approval (409) → draft → edit → approve → submit → editing the draft is refused after submit (409) → status

IDs (`resumeId`, `jobId`, `applicationId`) are captured automatically, so you can run the whole collection with the Collection Runner.

From the command line, with the [Postman CLI](https://learning.postman.com/docs/postman-cli/postman-cli-overview/):
```
postman collection run postman/ResumeAI.postman_collection.json
```
Set the upload file in the collection first. The CLI runner can't prompt for one.

Ollama on CPU takes about 20s to 2 min per AI step. The first job sync embeds every job, which takes a few minutes.

## Project layout

```
WebApplicationAI/
  Controllers/   Resumes, Jobs, Applications, AI (legacy /api/ai/ask)
  Services/      ResumeTextExtractor, JobFeedService (feeds), JobStore (sync + dedupe),
                 JobMatcher (scoring), RagService (chunking, embeddings, explanations, drafts)
  Data/          AppDbContext, entities, EF Core migrations
postman/         API test collection
```

## Known limitations

- Only companies listed in `JobSources` are searched. Employers on other hiring systems (Workday, Naukri-only, etc.) aren't covered yet.
- Salary isn't used in ranking, because these feeds rarely include it.
- Embeddings are compared in C#, not in the database. That's fine up to tens of thousands of passages; beyond that, switch to pgvector.
- A 3B local model sometimes words the summary loosely. The strengths, gaps and experience verdict are computed in code and are reliable.
