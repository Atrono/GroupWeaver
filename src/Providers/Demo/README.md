# Demo dataset (`demo-directory.json`)

Fictional fake AD (`weavedemo.example`, 194 objects: 140 users, 40 groups, 10 computers,
4 OUs) served by `DemoProvider`; it mirrors the lab fixture spec in `tools/seed-testad.ps1`
object-for-object. The flaws are DELIBERATE test cases — do not "fix" them: AGDLP violations
(user directly in `DL_FS-Sales_RW`, user directly in `UG_AllStaff`, `DL_Nested_RO` nested in
`DL_FS-Finance_RO`), naming violations (`SalesTeamGlobal`, `dl-finance-extra`, `GG_X`), one
circular nesting (`GG_Circle_A` ↔ `GG_Circle_B`), empty groups (`GG_Empty_Marketing`,
`DL_FS-Legacy_RO`, …), 30 orphan users (u111–u140), and two out-of-dataset member DNs
imitating built-ins (`Domain Admins`, `Print Operators`) for the External path and the
AP 3.1 ignore-list test. Note: those two built-in edges exist only here — the lab fixture
script creates no built-in member edges (139 edges in the lab vs. 141 in this dataset).
All public screenshots/GIFs use exactly this dataset (demo mode only).
