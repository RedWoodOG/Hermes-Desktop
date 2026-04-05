---
name: xlsx
description: Spreadsheet creation, reading, editing, formula support, and chart generation.
tools: bash, read_file, write_file, terminal
---

# Spreadsheet (.xlsx) Manipulation

You are an expert at creating and editing Excel spreadsheets using Python. You handle data, formulas, formatting, and charts.

## Setup

```bash
pip install openpyxl pandas 2>/dev/null
```

## Operations

### Create a New Spreadsheet

```python
import openpyxl
from openpyxl.styles import Font, Alignment, PatternFill, Border, Side
from openpyxl.utils import get_column_letter

wb = openpyxl.Workbook()
ws = wb.active
ws.title = "Data"

# Headers with formatting
headers = ['Name', 'Category', 'Amount', 'Date']
header_font = Font(bold=True, size=12)
header_fill = PatternFill(start_color="4472C4", end_color="4472C4", fill_type="solid")
header_font_white = Font(bold=True, size=12, color="FFFFFF")

for col, header in enumerate(headers, 1):
    cell = ws.cell(row=1, column=col, value=header)
    cell.font = header_font_white
    cell.fill = header_fill
    cell.alignment = Alignment(horizontal='center')

# Data rows
data = [
    ['Item A', 'Category 1', 150.00, '2024-01-15'],
    ['Item B', 'Category 2', 275.50, '2024-02-20'],
    ['Item C', 'Category 1', 89.99, '2024-03-10'],
]
for row_idx, row_data in enumerate(data, 2):
    for col_idx, value in enumerate(row_data, 1):
        ws.cell(row=row_idx, column=col_idx, value=value)

# Auto-fit column widths
for col in range(1, len(headers) + 1):
    ws.column_dimensions[get_column_letter(col)].width = 15

# Add a formula
ws.cell(row=len(data) + 2, column=2, value="Total:")
ws.cell(row=len(data) + 2, column=3, value=f"=SUM(C2:C{len(data)+1})")

wb.save("output.xlsx")
```

### Read a Spreadsheet

```python
import openpyxl

wb = openpyxl.load_workbook("input.xlsx")
print(f"Sheets: {wb.sheetnames}")

ws = wb.active
for row in ws.iter_rows(min_row=1, max_row=ws.max_row, values_only=True):
    print(row)
```

### Read with Pandas (for analysis)

```python
import pandas as pd

df = pd.read_excel("input.xlsx", sheet_name="Sheet1")
print(df.head())
print(df.describe())
```

### Edit an Existing Spreadsheet

```python
import openpyxl

wb = openpyxl.load_workbook("input.xlsx")
ws = wb.active

# Update a specific cell
ws['B2'] = 'New Value'

# Add a new column
ws.cell(row=1, column=ws.max_column + 1, value='New Column')

# Add formulas to new column
for row in range(2, ws.max_row + 1):
    ws.cell(row=row, column=ws.max_column, value=f"=C{row}*1.1")

wb.save("output.xlsx")
```

### Create Charts

```python
from openpyxl.chart import BarChart, Reference

wb = openpyxl.load_workbook("data.xlsx")
ws = wb.active

chart = BarChart()
chart.title = "Sales by Category"
chart.x_axis.title = "Category"
chart.y_axis.title = "Amount"

data_ref = Reference(ws, min_col=3, min_row=1, max_row=ws.max_row)
cats_ref = Reference(ws, min_col=1, min_row=2, max_row=ws.max_row)

chart.add_data(data_ref, titles_from_data=True)
chart.set_categories(cats_ref)
chart.style = 10
chart.width = 20
chart.height = 12

ws.add_chart(chart, "E2")
wb.save("with_chart.xlsx")
```

### Multiple Sheets

```python
import openpyxl

wb = openpyxl.Workbook()
ws1 = wb.active
ws1.title = "Summary"

ws2 = wb.create_sheet("Details")
ws3 = wb.create_sheet("Raw Data")

# Cross-sheet formulas
ws1['A1'] = "Total from Details"
ws1['B1'] = "=Details!B10"

wb.save("multi_sheet.xlsx")
```

### CSV to Excel Conversion

```python
import pandas as pd

df = pd.read_csv("input.csv")
df.to_excel("output.xlsx", index=False, sheet_name="Data")
```

## Workflow

1. **Understand the data** - What data? What structure? What output format?
2. **Read existing files** if editing (check sheet names, column headers, data types)
3. **Create/modify the spreadsheet** with proper formatting
4. **Add formulas** where appropriate (SUM, AVERAGE, VLOOKUP, etc.)
5. **Verify** - Read back the file to confirm correctness
6. **Report** - Provide the output file path and summary of contents

## Formatting Tips

- Always bold and style header rows
- Set column widths to fit content
- Use number formatting for currency (`$#,##0.00`) and dates
- Freeze the top row for large datasets: `ws.freeze_panes = 'A2'`
- Use conditional formatting for highlighting
- Add filters: `ws.auto_filter.ref = ws.dimensions`
