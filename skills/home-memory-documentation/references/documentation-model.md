# Home Memory documentation model

Use this reference when a Home Memory task requires a decision about record boundaries, relationships, or information placement. The examples illustrate the model and do not prescribe a building structure, naming scheme, or level of detail.

## 1. Representing the real world

### 1.1 Elements represent places, objects, and components

An element represents something with its own identity in the documentation. It may be a place, such as a house, floor, or room. It may also be a device, piece of furniture, tool, tree, or component of a larger object.

Elements form a tree. Their placement explains where something is or what it is part of. For example:

```text
House
|-- Basement
|   `-- Utility room
|       `-- Electrical panel
`-- Ground floor
    `-- Bathroom
        `-- Ceiling light
```

These levels are not a mandatory template. A single room, apartment, workshop, or installation may be the appropriate starting point. Add another level only when it represents a useful location or actual assembly.

Each element has at most one immediate parent. A light being in the bathroom and belonging to the electrical installation does not require a duplicate under another branch. Location, category, and lines provide different views of the same object.

An element path follows from the hierarchy and names. It helps with navigation but can change when an element is renamed or moved. When editing a record that has already been found, use its OID as the stable reference.

### 1.2 Lines describe physical runs

A line represents a physical run between two elements, such as a pipe, cable, conduit, or ventilation duct. It can also carry a route and length. This answers questions the location tree alone cannot answer.

For the example, the electrical panel and ceiling light retain their actual locations. The supply cable is a line between them:

```text
Electrical panel -> Cable to ceiling light -> Bathroom ceiling light
```

A run is useful as a line when its endpoints, route, length, or technical specification matter. Not every visible appliance lead needs a record. A known concealed route should not disappear into a general description when the user will later want to find it by its endpoints.

Use existing devices as endpoints where appropriate. A separate terminal or pipe-end element is useful only when that point has relevant identity of its own. Do not create artificial endpoint objects merely because a line needs two endpoints. If the endpoints are not confirmed, record only the known information on the appropriate existing object and leave the relationship unresolved.

Treat cables and conduits separately. A cable is the electrical run; a conduit may provide its routing enclosure. If both are relevant and confirmed, record separate lines. They need not have identical endpoints. Multiple cores in one cable do not become multiple cable lines. Nearby or identical endpoints do not prove that a particular cable occupies a particular conduit.

Choose a coherent documentation direction suited to the medium and function. This is a documentation convention, not a universal claim about physical flow or network topology. Power, bus, network, control, and drinking-water runs are commonly described from a source or distribution point toward a consumer or outlet. Wastewater and rainwater follow actual drainage. For ventilation, the documentation direction may run from the unit to the room terminal while the name identifies supply or extract air. A bus, network, or ring need not have one physical source. Line name and endpoint fields should express the same chosen direction.

Conduit occupancy requires positive evidence. A missing cable record does not establish that a conduit is empty. A stated future purpose also does not establish current occupancy. Preserve the user's terminology, and do not turn a local use of a term such as `Leerrohr` into a universal definition.

### 1.3 Location, category, line, and status answer different questions

| Information | Question | Example |
|---|---|---|
| Position in the tree | Where is it, or what is it part of? | Light in the bathroom |
| Category | What kind of object is it? | Light within a lighting category |
| Line | Which physical run joins which endpoints? | Cable from panel to light |
| Status | Which known lifecycle state applies? | Planned or removed |

Categories may be hierarchical. They provide shared views of similar objects in different locations. A category is not a second installation location and does not by itself establish membership of a particular system.

Use actual category and status names from the user's data. Ordinary existing objects do not all need an explicit status as a precaution. Unknown, unchecked, and unclear occupancy describe the state of knowledge, not automatically a lifecycle status.

## 2. Deciding what needs its own record

### 2.1 Let later usefulness determine the detail

A separate record is useful when something should be found, distinguished, described, connected, or tracked independently. Size, price, product identity, or category alone do not decide this.

Consider whether the separate record provides concrete value:

- The user needs to find or distinguish the object specifically later.
- It has relevant characteristics, a location, or a history of its own.
- It is needed as an endpoint of a documented line.
- Keeping the information only on its parent would make it unclear or difficult to retrieve.

These are decision criteria, not a mandatory interview. The task often already supplies the answer.

### 2.2 Applying the criteria

A ceiling light deserves its own record when the user wants to identify it, describe its installation, or connect a documented cable to it. An existing electrical panel should be reused as the other endpoint. The cable becomes a line if its route or specification should remain retrievable. A conduit may warrant a separate line when its route or future usefulness matters.

The light's mounting screws normally do not need individual elements. A relevant installation detail can be described on the light. A particular mounting component may deserve its own record if it is inspected, replaced, or tracked separately.

A replaceable bulb can be a child of the light if its exact identity or individual replacements should be documented. If the user only wants to know which bulb fits, that information can often remain on the light. A component mentioned in a manual is not by itself a reason to split the device into all its parts.

For runs, later usefulness also sets the detail. Known branches that matter to the task justify separate endpoints and segments. If a branching route is simplified into several lines from a central point, do not add shared segments together as if each were physically separate. Make the simplification clear where it affects interpretation.

### 2.3 Example: split air conditioner

An indoor and outdoor unit retain their actual locations in the element tree. Refrigerant, power, control, and condensate runs are considered separately according to their function and confirmed endpoints. The shared label `air conditioning` does not replace these relationships. Do not infer complete routes, connection points, or lengths merely from the existence of both units.

### 2.4 Refine existing documentation carefully

Start with the structure useful for the task. Additional information may later justify separate components or more precise lines. Before adding records, inspect whether existing records already describe the same object.

Preserve useful existing names and conventions. Do not introduce a new naming scheme for the entire home because an example uses a different one. A broader cleanup is a separate task.

## 3. Placing information

### 3.1 Identify what a statement describes

Before choosing a field, establish whether the information describes a place, actual object, physical run, operation, or individual event. Give information one authoritative home. A short identifying phrase may repeat in a name without becoming a second full description to maintain.

For example:

- Manufacturer and product model identify the product used for an actual element.
- Serial number and installation details describe that individual element.
- Route and measured length describe the actual line.
- Operating instructions describe repeated use or maintenance.
- A dated replacement describes a completed event.

Empty fields are acceptable. Do not add assumed technical data or generic maintenance instructions merely to make a record look complete.

Field availability defines where information belongs when it is relevant; it does not make the information necessary to collect. A missing optional value is not automatically a documentation defect. Do not ask the user to measure, research, or reconstruct it unless the value affects the requested decision or action, or supports a concrete later use identified by the user. Do not routinely present omitted optional values as open points, warnings, or follow-up tasks. Relevant information already available may be recorded without additional questioning.

### 3.2 Shared element and line fields

| Field or relationship | Purpose |
|---|---|
| `name` | Recognizable name that distinguishes the object sufficiently |
| `short_name` | Shorter element name in the path, where useful |
| Parent element | Spatial location or membership as a component |
| `position` | Useful detail within the parent element |
| `category` | Classification by kind |
| `status` | Explicitly known lifecycle state |
| `purpose` | Enduring function not already clear from the identity |
| `description` | Lasting technical information and observations |
| `user_manual` | Sourced instructions for operation and recurring maintenance |
| `source`, `destination` | Actual endpoints of a line |
| `route` | Relevant path between the endpoints |
| `length` | Length of the actual run, when known |

The hierarchy already supplies location context, so a child name need not always repeat its room. Keep enough context for recognition and preserve established naming conventions.

Follow an established project convention when naming a line. Otherwise, prefer the run kind, an optional distinguishing purpose, and short names for source and destination, in that order where natural in the user's language. The name should remain understandable in a list or search result without endpoint columns. Avoid repeating context already clear from the whole name; preserve useful established identifiers such as circuit numbers. Full paths belong in the endpoint relationships and intermediate routing details in `route`. Check each name, including names copied from drafts or imports, against its actual endpoints and documentation direction before saving.

`route` complements the endpoints. It does not need to repeat their complete paths or an endpoint's documented position. A known length does not establish an unknown route.

### 3.3 Home Memory field mapping

HomeMemoryMCP does not provide a workflow to create or assign product types. When no authoritative type information is already exposed, keep confirmed manufacturer and product model details in the actual element's `description`. Treat any existing type information returned by the MCP as read-only context and do not duplicate it unnecessarily.

Use these placements:

| Information | Home Memory location |
|---|---|
| Manufacturer and product model | Element `description`, unless already authoritative elsewhere |
| Serial number and installation detail | Element `description` |
| Confirmed completed work with date | Lasting entry in element `description` |
| Short-term work to do | Element `note` |
| Applicable operating and maintenance instructions | Element `user_manual` |
| Cable or conduit specification | Line `description`, unless another field already covers it |
| Actual route and measured length | Line `route` and `length` |

Use `note` for brief temporary information, such as an unresolved check. An enduring intended use belongs in `purpose`. For example, `For a future socket` describes why a conduit exists; it does not mean that installing the socket is currently an open task or that the conduit is confirmed empty. Remove or update a temporary note once the work or check is complete. A confirmed completed job should not remain as an open to-do.

## 4. Explaining a decision

Explain only the decision needed for the current task. For example:

> The light stays in the bathroom, and the electrical panel stays in the utility room. We record the cable between them as a line. This lets you find both the light and its cable route later. If the conduit also matters, we document it separately with its own confirmed endpoints.

Account for the complete model when working, but do not introduce every field or the entire structure before a small change.
