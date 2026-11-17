---
type: C4 Context
title: Hard "quotes" & <angles>
description: "Línea — ünïcode ✓"
tags: [a, b]
generated: { by: test/1.0, at: 2026-11-04 }
verified: { by: human:ryan, at: "2026-11-04T10:00:00Z" }
---

```mermaid
C4Context
Person(user, "User")
System(shop, "Shop", "Sells things", $link="sub/cont.md")
System_Ext(pay, "Payments")
Rel(user, shop, "Buys from")
Rel(shop, pay, "Charges")
```

Intro with <b>raw html</b> & ampersand, "quotes", 'single', a [titled link](sub/cont.md "the title") and <script>alert(1)</script>.

| col a | col b | c |
|:--|--:|:-:|
| 1 | `two` | **3** |
| x<y | y&z | |

```python
def f():
    return "<tag>"
```

* item one
* item two
  1. nested

> quote line
> more

<div>block html</div>

Line one  
hard break and *emph* __strong__ and https://example.com bare and ![img](x.png) and [js](javascript:alert(1)).

## shop

The thing we build. See [cont](sub/cont.md).

## `pay`

Payments note <i>x</i>.
