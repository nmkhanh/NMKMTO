# MODEL

MODEL lay dien tich san va khoi luong san theo sheet duoc check.

- Sheet duoc gom theo `Level + Zone` de tranh tinh lap TOP/BOTTOM/SHEAR.
- Bat buoc co view `MTO FILLED REGION AREA`.
- FilledRegion trong view MTO tao thanh solid theo level va offset.
- Floor duoc lay tu tat ca Revit Link, bo type co `PRECAST` hoac `PLINTH`.
- Floor thuong tinh volume theo solid giao that.
- Floor `SLIMDECK` tinh area theo mat tren that, volume theo:

`V = A x (H - 170) / 1000`

- `A`: dien tich Slimdeck, m2.
- `H`: tong chieu day Slimdeck, mm.
- Neu checkbox `3D` bat thi tao DirectShape.
- Neu co warning moi xuat file `_WARNING.csv`.
