#!/usr/bin/env python3
"""
A/B Testing: Monolithic Decision Head (Plan A) vs. Speculative Heads (Plan B, from jev-ultrafast)
Evaluates latency, token efficiency, and decision precision on real Windows GUI UIA elements.
"""

import json
import os
import subprocess
import time
from typing import Any, Dict, List, Tuple
from typesafe_sdk import TypeSafeClient, Choice, Noul

def _load_api_key() -> str:
    key = os.environ.get("TYPESAFE_API_KEY", "")
    if not key and os.name == "nt":
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Environment") as env_k:
                val, _ = winreg.QueryValueEx(env_k, "TYPESAFE_API_KEY")
                if val:
                    key = str(val).strip()
        except Exception:
            pass
    return key

API_KEY = _load_api_key()
if not API_KEY:
    raise ValueError("TYPESAFE_API_KEY environment variable is not set. Please set it before running.")
os.environ["TYPESAFE_API_KEY"] = API_KEY

WIN_EXE = r"E:\ClaudeCode\skills\huashu-win-use\scripts\win.exe"


class ABTestBench:
    def __init__(self):
        self.client = TypeSafeClient()

    def get_uia_elements(self, target: str = "ChatCut") -> Tuple[int, str, List[Dict[str, Any]]]:
        """Runs win.exe see and extracts real UIA elements."""
        res = subprocess.run([WIN_EXE, "see", target], capture_output=True, text=True, encoding="utf-8")
        lines = res.stdout.strip().split("\n")
        hwnd = None
        json_path = None

        for line in lines:
            if line.startswith("receipt="):
                hwnd = int(line.split("=")[1].split(":")[0])
            if "@" in line and ".json" in line:
                idx = line.find("@")
                end_idx = line.find(".json", idx)
                if idx != -1 and end_idx != -1:
                    json_path = line[idx + 1:end_idx + 5].strip()

        if hwnd and not json_path:
            temp_dir = os.environ.get("TEMP", r"C:\Users\26502\AppData\Local\Temp")
            cand = os.path.join(temp_dir, f"see-{hwnd}.png.json")
            if os.path.exists(cand):
                json_path = cand

        elements = []
        if json_path and os.path.exists(json_path):
            with open(json_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            elements = data.get("elements", [])

        return hwnd, json_path, elements

    # -------------------------------------------------------------
    # Plan A: Monolithic Decision Head (Baseline)
    # All elements lumped into one single Choice question
    # -------------------------------------------------------------
    def decide_plan_a_monolithic(self, goal: str, elements: List[Dict[str, Any]]) -> Dict[str, Any]:
        criteria = {}
        candidate_list = []
        for el in elements:
            ref = el.get("Ref")
            role = el.get("Role", "Element")
            title = (el.get("Title") or "").strip()
            enabled = el.get("Enabled", True)
            if role in ("Window", "Pane", "Group") and not title:
                continue
            title_str = title if title else "(unnamed)"
            state_str = " [disabled]" if not enabled else ""
            criteria[ref] = f"{role} '{title_str}'{state_str}"
            candidate_list.append({"ref": ref, "role": role, "title": title_str})

        criteria["none"] = "None of the visible elements are appropriate"
        criteria["done"] = "The user goal is already satisfied on screen"

        t0 = time.time()
        resp = self.client.system_one(
            state={
                "user_goal": goal,
                "candidate_elements": candidate_list
            },
            questions={
                "target_element": Choice(
                    instructions=f"Which element should be acted upon to achieve: '{goal}'?",
                    criteria=criteria
                ),
                "action": Choice(
                    instructions="What UI interaction is required?",
                    criteria={
                        "click": "Click the element",
                        "type": "Type text into field",
                        "hover": "Hover over element",
                        "finish": "Goal already achieved"
                    }
                ),
                "is_goal_achieved": Noul(instructions=f"Has goal '{goal}' already been accomplished?")
            }
        )
        latency = time.time() - t0

        chosen_ref = resp.answers["target_element"].choice
        action = resp.answers["action"].choice
        confidence = getattr(resp.answers["target_element"], "confidence", 1.0)
        target_el = next((el for el in elements if el.get("Ref") == chosen_ref), None)

        return {
            "plan": "Plan A (Monolithic)",
            "latency": latency,
            "chosen_ref": chosen_ref,
            "action": action,
            "confidence": confidence,
            "target_title": target_el.get("Title") if target_el else None,
            "target_role": target_el.get("Role") if target_el else None,
            "input_tokens": getattr(resp.usage, "input_tokens", 0),
            "output_tokens": getattr(resp.usage, "output_tokens", 0),
        }

    # -------------------------------------------------------------
    # Plan B: Speculative Heads (from browser-use/jev-ultrafast)
    # Segregates clickables from editables into speculative action heads
    # Evaluated in a single network round-trip
    # -------------------------------------------------------------
    def decide_plan_b_speculative(self, goal: str, elements: List[Dict[str, Any]]) -> Dict[str, Any]:
        click_criteria = {}
        type_criteria = {}

        click_candidates = []
        type_candidates = []

        for el in elements:
            ref = el.get("Ref")
            role = el.get("Role", "Element")
            title = (el.get("Title") or "").strip()
            enabled = el.get("Enabled", True)

            if not title and role in ("Window", "Pane", "Group", "Custom"):
                continue

            title_str = title if title else "(unnamed)"
            state_str = " [disabled]" if not enabled else ""
            desc = f"{role} '{title_str}'{state_str}"

            # Segregate into specialized target pools
            if role in ("Edit", "TextBox", "ComboBox") or "输入" in title or "搜索" in title or "命名" in title:
                type_criteria[ref] = desc
                type_candidates.append({"ref": ref, "title": title_str})
            else:
                click_criteria[ref] = desc
                click_candidates.append({"ref": ref, "title": title_str})

        click_criteria["none"] = "No clickable element matches"
        type_criteria["none"] = "No editable element matches"

        questions = {
            "operation": Choice(
                instructions=f"Select the next atomic UI operation for goal: '{goal}'",
                criteria={
                    "CLICK": "Click a button, menu, tab or hyperlink",
                    "TYPE_TEXT": "Type text into a textbox, input or search field",
                    "HOVER": "Hover over a dropdown menu or toolbar",
                    "DONE": "The goal is already achieved, no action needed"
                }
            ),
            "click_target": Choice(
                instructions=f"If operation is CLICK or HOVER, which element is the target for: '{goal}'?",
                criteria=click_criteria
            ),
            "is_goal_achieved": Noul(instructions=f"Has goal '{goal}' been achieved?")
        }

        # Only attach type_text_target head if editable elements exist
        if type_criteria:
            questions["type_target"] = Choice(
                instructions=f"If operation is TYPE_TEXT, which input field is the target for: '{goal}'?",
                criteria=type_criteria
            )

        t0 = time.time()
        resp = self.client.system_one(
            state={
                "user_goal": goal,
                "clickables_count": len(click_candidates),
                "editables_count": len(type_candidates)
            },
            questions=questions
        )
        latency = time.time() - t0

        operation = resp.answers["operation"].choice
        if operation == "TYPE_TEXT" and "type_target" in resp.answers:
            chosen_ref = resp.answers["type_target"].choice
            confidence = getattr(resp.answers["type_target"], "confidence", 1.0)
        else:
            chosen_ref = resp.answers["click_target"].choice
            confidence = getattr(resp.answers["click_target"], "confidence", 1.0)

        target_el = next((el for el in elements if el.get("Ref") == chosen_ref), None)

        return {
            "plan": "Plan B (Speculative Heads)",
            "latency": latency,
            "chosen_ref": chosen_ref,
            "action": operation,
            "confidence": confidence,
            "target_title": target_el.get("Title") if target_el else None,
            "target_role": target_el.get("Role") if target_el else None,
            "input_tokens": getattr(resp.usage, "input_tokens", 0),
            "output_tokens": getattr(resp.usage, "output_tokens", 0),
        }

    def run_benchmark(self, tasks: List[Dict[str, str]]):
        hwnd, json_path, elements = self.get_uia_elements("ChatCut")
        print(f"Target Window: ChatCut (HWND: {hwnd}) | Total UIA Elements: {len(elements)}")
        print("=" * 80)

        results = []

        for idx, task in enumerate(tasks, start=1):
            goal = task["goal"]
            expected_ref = task["expected_ref"]
            print(f"\n[Task {idx}/{len(tasks)}] Goal: {goal} (Expected: {expected_ref})")

            # Run Plan A
            res_a = self.decide_plan_a_monolithic(goal, elements)
            hit_a = res_a["chosen_ref"] == expected_ref
            print(f"  Plan A (Monolithic)  : {res_a['latency']:.3f}s | Token: {res_a['input_tokens']} | Picked: {res_a['chosen_ref']} ({res_a['target_role']} '{res_a['target_title']}') | Action: {res_a['action']} | Hit: {hit_a}")

            # Run Plan B
            res_b = self.decide_plan_b_speculative(goal, elements)
            hit_b = res_b["chosen_ref"] == expected_ref
            print(f"  Plan B (Speculative) : {res_b['latency']:.3f}s | Token: {res_b['input_tokens']} | Picked: {res_b['chosen_ref']} ({res_b['target_role']} '{res_b['target_title']}') | Action: {res_b['action']} | Hit: {hit_b}")

            speedup = ((res_a['latency'] - res_b['latency']) / res_a['latency']) * 100
            token_saved = ((res_a['input_tokens'] - res_b['input_tokens']) / res_a['input_tokens']) * 100
            print(f"  --> Speedup: {speedup:+.1f}% | Token Saved: {token_saved:+.1f}%")

            results.append({
                "task": goal,
                "expected": expected_ref,
                "plan_a": res_a,
                "plan_b": res_b,
                "hit_a": hit_a,
                "hit_b": hit_b
            })

        return results


if __name__ == "__main__":
    benchmark_tasks = [
        {"goal": "点击 Help 菜单查看关于与帮助文档", "expected_ref": "e5"},
        {"goal": "复制当前项目的文件路径", "expected_ref": "e9"},
        {"goal": "在重命名输入框中修改项目名称", "expected_ref": "e10"},
        {"goal": "打开项目库浏览所有剪辑项目", "expected_ref": "e7"},
        {"goal": "切换到 Edit 菜单进行撤销或重做操作", "expected_ref": "e2"},
    ]

    bench = ABTestBench()
    bench.run_benchmark(benchmark_tasks)
