# Auspicious Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate the BaziFlow Fortune module into the transit Map UI to highlight "Auspicious Routes" that align with favorable directions and times.

**Architecture:** We will mock the backend schema changes by injecting `FavorableDirections` and `LuckyHours` into the `FortuneData` client model. The routing UI (`Map.razor`) will evaluate routes against this data and display a badge if they align.

**Tech Stack:** C#, Blazor WebAssembly

## Global Constraints
- Target Framework: .NET 10.0
- Client-Side Blazor
- C# 12+ Features allowed

---

### Task 1: Update BaziFlow Data Models

**Files:**
- Modify: `MyTransportAppWASM/Models/BaziFlow/BaziFlowModels.cs`

**Interfaces:**
- Consumes: None
- Produces: `FortuneData` with `FavorableDirections` and `LuckyHours`

- [ ] **Step 1: Update the Model**

Add the properties to `FortuneData`.

```csharp
    public record FortuneData
    {
        public string Almanac { get; set; } = string.Empty;
        public string Analysis { get; set; } = string.Empty;
        public List<string> FavorableDirections { get; set; } = new();
        public List<int> LuckyHours { get; set; } = new();
    }
```

- [ ] **Step 2: Commit**

```bash
git add MyTransportAppWASM/Models/BaziFlow/BaziFlowModels.cs
git commit -m "feat: add Auspicious routing fields to FortuneData"
```

---

### Task 2: Mock Data in Service

**Files:**
- Modify: `MyTransportAppWASM/Services/BaziFlowService.cs`

**Interfaces:**
- Consumes: `FortuneData` model changes
- Produces: `GetDateFortuneAsync` returning mocked favorable directions.

- [ ] **Step 1: Inject Mock Data**

In `BaziFlowService.cs`, update `GetDateFortuneAsync`.

```csharp
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ApiResponse<FortuneData>>();
                    if (result?.Data != null) 
                    {
                        // Mock fields until backend updates
                        result.Data.FavorableDirections = new List<string> { "North", "East", "Southeast" };
                        result.Data.LuckyHours = new List<int> { 7, 8, 9, 17, 18 };
                    }
                    return result?.Data;
                }
```

- [ ] **Step 2: Commit**

```bash
git add MyTransportAppWASM/Services/BaziFlowService.cs
git commit -m "feat: mock FavorableDirections and LuckyHours in service"
```

---

### Task 3: Map View Model Update

**Files:**
- Modify: `MyTransportAppWASM/Pages/Map/Map.razor.cs`

**Interfaces:**
- Consumes: `IBaziFlowService`, `FortuneData`
- Produces: `RouteOption` with an `IsAuspicious` flag (since the actual route bearing requires a complex calculation not currently exposed, we will add a mocked check based on the route index for UI testing purposes, or check if the current time is a lucky hour).

- [ ] **Step 1: Inject Service and Load Fortune**

Inject `IBaziFlowService` and add `TodayFortune`.

```csharp
        [Inject]
        public IBaziFlowService BaziFlowService { get; set; } = default!;

        public FortuneData? TodayFortune { get; set; }
```

In `OnInitializedAsync`, fetch it:
```csharp
            try {
                TodayFortune = await BaziFlowService.GetDateFortuneAsync(DateTime.Now.ToString("yyyy-MM-dd"));
            } catch { }
```

- [ ] **Step 2: Update Route Option Model**

Somewhere in `Map.razor.cs`, there is a `RouteOption` or similar class. Find it and add `public bool IsAuspicious { get; set; }`. When populating `_routeOptions`, set `IsAuspicious` to true if the current hour is in `TodayFortune.LuckyHours` or just alternate it for the UI demo.

- [ ] **Step 3: Commit**

```bash
git add MyTransportAppWASM/Pages/Map/Map.razor.cs
git commit -m "feat: evaluate route auspiciousness in Map page"
```

---

### Task 4: UI Integration

**Files:**
- Modify: `MyTransportAppWASM/Pages/Map/Map.razor`

**Interfaces:**
- Consumes: `RouteOption.IsAuspicious`

- [ ] **Step 1: Add the Badge**

Inside the `foreach (var route in _routeOptions)` loop, near the Live/Scheduled badge, add the Auspicious badge.

```html
<div class="d-flex justify-content-between align-items-center">
    <strong style="color: var(--text-color);">Option @(route.OriginalRoute.RouteId + 1)</strong>
    <div>
        @if (route.IsAuspicious)
        {
            <span class="badge bg-warning text-dark me-1" title="Aligns with lucky directions or times">🌟 Auspicious</span>
        }
        <span class="badge @(route.HasLiveBus ? "bg-success" : "bg-secondary")">
            @(route.HasLiveBus ? "Live" : "Scheduled")
        </span>
    </div>
</div>
```

- [ ] **Step 2: Commit**

```bash
git add MyTransportAppWASM/Pages/Map/Map.razor
git commit -m "feat: display Auspicious Badge on lucky routes"
```
