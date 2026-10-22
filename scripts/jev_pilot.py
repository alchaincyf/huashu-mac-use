#!/usr/bin/env python3
"""
huashu-win-use Jev Pilot (Fast Decision Layer for Windows GUI Automation)
Integrates TypeSafe AI's Jev model with huashu-win-use's win.exe and cdp.js
to eliminate multi-second vision-LLM latency in computer-use loops.
"""

import argparse
import json
import os
import subprocess
import sys
import time
from typing import Any, Dict, List, Optional, Tuple
from typesafe_sdk import TypeSafeClient, Choice, Noul, Score

SKILL_SCRIPTS_DIR = os.path.dirname(os.path.abspath(__file__))
WIN_EXE = os.path.join(SKILL_SCRIPTS_DIR, "win.exe")


def _load_api_key(passed_key: Optional[str] = None) -> str:
    if passed_key:
        return passed_key
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


class JevPilot:
    def __init__(self, api_key: Optional[str] = None):
        self.api_key = _load_api_key(api_key)
        if not self.api_key:
            raise ValueError("TYPESAFE_API_KEY environment variable is not set. Pass --api-key or set TYPESAFE_API_KEY.")
        os.environ["TYPESAFE_API_KEY"] = self.api_key
        self.client = TypeSafeClient()

    def capture_and_parse(self, target: str) -> Tuple[Optional[int], Optional[str], List[Dict[str, Any]], float]:
        """Runs win.exe see and extracts UIA elements."""
        t0 = time.time()
        proc = subprocess.run(
            [WIN_EXE, "see", target],
            capture_output=True,
            text=True,
            encoding="utf-8"
        )
        t_capture = time.time() - t0

        if proc.returncode != 0:
            print(f"[Error] win see failed (code {proc.returncode}): {proc.stderr.strip() or proc.stdout.strip()}")
            return None, None, [], t_capture

        lines = proc.stdout.strip().split("\n")
        hwnd = None
        json_path = None

        for line in lines:
            if line.startswith("receipt="):
                parts = line.split("=")[1].split(":")
                hwnd = int(parts[0])
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
            try:
                with open(json_path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                elements = data.get("elements", [])
            except Exception as e:
                print(f"[Warning] Failed to read UIA JSON: {e}")

        return hwnd, json_path, elements, t_capture

    def decide_next_action(
        self,
        goal: str,
        elements: List[Dict[str, Any]],
        context: Optional[str] = None
    ) -> Tuple[Dict[str, Any], float]:
        """
        Calls Jev System One to rapidly select the next target element and action.
        """
        # Build specialized criteria pools: Clickable vs Editable
        click_criteria = {}
        type_criteria = {}
        click_candidates = []
        type_candidates = []

        for el in elements:
            ref = el.get("Ref")
            role = el.get("Role", "Element")
            title = (el.get("Title") or "").strip()
            enabled = el.get("Enabled", True)

            # Skip large container panes without names
            if not title and role in ("Window", "Pane", "Group", "Custom"):
                continue

            title_str = title if title else "(unnamed)"
            state_str = " [disabled]" if not enabled else ""
            desc = f"{role} '{title_str}'{state_str}"

            if role in ("Edit", "TextBox", "ComboBox") or any(kw in title for kw in ("输入", "搜索", "命名", "edit", "search")):
                type_criteria[ref] = desc
                type_candidates.append({"ref": ref, "title": title_str})
            else:
                click_criteria[ref] = desc
                click_candidates.append({"ref": ref, "title": title_str})

        click_criteria["none"] = "No clickable element matches the user goal"
        type_criteria["none"] = "No editable element matches the user goal"

        questions = {
            "operation": Choice(
                instructions=f"Select the next atomic UI operation for goal: '{goal}'",
                criteria={
                    "CLICK": "Click a button, menu item, tab, or link",
                    "TYPE_TEXT": "Type text into an input box or search field",
                    "HOVER": "Hover over element to reveal menu/sub-options",
                    "DONE": "The goal is already completely fulfilled"
                }
            ),
            "click_target": Choice(
                instructions=f"If operation is CLICK or HOVER, which element is the target for: '{goal}'?",
                criteria=click_criteria
            ),
            "is_goal_achieved": Noul(
                instructions=f"Has the user goal '{goal}' already been completely fulfilled?"
            )
        }

        if type_criteria:
            questions["type_target"] = Choice(
                instructions=f"If operation is TYPE_TEXT, which input field is the target for: '{goal}'?",
                criteria=type_criteria
            )

        state_data: Dict[str, Any] = {
            "user_goal": goal,
            "clickables_count": len(click_candidates),
            "editables_count": len(type_candidates)
        }
        if context:
            state_data["session_context"] = context

        t0 = time.time()
        response = self.client.system_one(state=state_data, questions=questions)
        t_decision = time.time() - t0

        operation = response.answers["operation"].choice
        if operation == "TYPE_TEXT" and "type_target" in response.answers:
            target_ref = response.answers["type_target"].choice
            confidence = getattr(response.answers["type_target"], "confidence", 1.0)
            norm_action = "type"
        elif operation == "HOVER":
            target_ref = response.answers["click_target"].choice
            confidence = getattr(response.answers["click_target"], "confidence", 1.0)
            norm_action = "hover"
        elif operation == "DONE":
            target_ref = "done"
            confidence = getattr(response.answers["operation"], "confidence", 1.0)
            norm_action = "finish"
        else:
            target_ref = response.answers["click_target"].choice
            confidence = getattr(response.answers["click_target"], "confidence", 1.0)
            norm_action = "click"

        target_info = next((el for el in elements if el.get("Ref") == target_ref), None)

        result = {
            "target_ref": target_ref,
            "target_info": target_info,
            "action": norm_action,
            "operation": operation,
            "is_goal_achieved": response.answers["is_goal_achieved"].noul,
            "confidence": confidence,
            "raw_answers": response.answers,
            "usage": {
                "input_tokens": getattr(response.usage, "input_tokens", 0),
                "output_tokens": getattr(response.usage, "output_tokens", 0)
            }
        }
        return result, t_decision

    def execute_action(
        self,
        hwnd: int,
        target_ref: str,
        action: str,
        json_path: str,
        dry_run: bool = True,
        text_to_type: Optional[str] = None
    ) -> Tuple[int, str, float]:
        """Executes the action using win.exe."""
        t0 = time.time()
        cmd = [WIN_EXE]

        if action == "click":
            cmd.extend(["clickin", str(hwnd), f"{target_ref}@{json_path}"])
        elif action == "hover":
            cmd.extend(["hoverin", str(hwnd), f"{target_ref}@{json_path}"])
        elif action == "type" and text_to_type:
            # Type text using win.exe type or op
            cmd.extend(["type", str(hwnd), text_to_type])
        else:
            cmd.extend(["clickin", str(hwnd), f"{target_ref}@{json_path}"])

        if dry_run:
            cmd.append("--dry")

        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
        t_exec = time.time() - t0
        output = proc.stdout.strip() or proc.stderr.strip()
        return proc.returncode, output, t_exec

    def step(
        self,
        target: str,
        goal: str,
        dry_run: bool = True,
        text_to_type: Optional[str] = None
    ) -> Dict[str, Any]:
        """Single-step fast decision & execution."""
        print(f"\n=======================================================")
        print(f"[Jev Pilot] Target: {target} | Goal: '{goal}'")
        print(f"Mode: {'DRY RUN (Simulation)' if dry_run else 'LIVE EXECUTION'}")
        print(f"-------------------------------------------------------")

        # Step 1: Capture & Parse
        hwnd, json_path, elements, t_capture = self.capture_and_parse(target)
        print(f"[Capture] win see finished in {t_capture:.3f}s (HWND: {hwnd}, Elements: {len(elements)})")

        if not elements:
            print("[Warning] No UIA elements detected for this window.")
            return {"success": False, "error": "No elements found"}

        # Step 2: Jev Decision
        decision, t_decision = self.decide_next_action(goal, elements)
        print(f"[Decision] Jev System One finished in {t_decision:.3f}s")
        print(f"  Target Ref : {decision['target_ref']}")
        if decision['target_info']:
            ti = decision['target_info']
            print(f"  Element    : {ti.get('Role')} \"{ti.get('Title')}\" at ({ti.get('Cx')}, {ti.get('Cy')})")
        print(f"  Action     : {decision['action']}")
        print(f"  Confidence : {decision['confidence']}/2 (Tokens in:{decision['usage']['input_tokens']}, out:{decision['usage']['output_tokens']})")
        print(f"  Goal Done? : {decision['is_goal_achieved']:.2f}")

        # Step 3: Action Execution
        target_ref = decision["target_ref"]
        if target_ref in ("none", "done"):
            print(f"[Result] Jev decided no further action is needed ({target_ref}).")
            return {
                "success": True,
                "completed": True,
                "decision": decision,
                "latency": {"capture": t_capture, "decision": t_decision, "total": t_capture + t_decision}
            }

        retcode, out, t_exec = self.execute_action(
            hwnd=hwnd,
            target_ref=target_ref,
            action=decision["action"],
            json_path=json_path,
            dry_run=dry_run,
            text_to_type=text_to_type
        )
        print(f"[Execution] Finished in {t_exec:.3f}s (Exit code: {retcode})")
        print(f"  Output: {out}")

        total_latency = t_capture + t_decision + t_exec
        print(f"-------------------------------------------------------")
        print(f"[Summary] Total Step Latency: {total_latency:.3f}s (vs 15s~30s in vision-LLM)")
        print(f"=======================================================\n")

        return {
            "success": retcode == 0,
            "decision": decision,
            "latency": {
                "capture": t_capture,
                "decision": t_decision,
                "execution": t_exec,
                "total": total_latency
            }
        }


def main():
    parser = argparse.ArgumentParser(description="huashu-win-use Jev Pilot: Sub-second Computer-Use Decision Layer")
    parser.add_argument("target", help="Target window owner keyword or HWND (e.g. ChatCut, Zoom, 855400)")
    parser.add_argument("--goal", required=True, help="User goal description (e.g. 'Click the Help menu')")
    parser.add_argument("--execute", action="store_true", help="Execute the action for real (default is dry-run)")
    parser.add_argument("--type-text", help="Text to type if action is typing")
    parser.add_argument("--api-key", help="TypeSafe Jev API Key")

    args = parser.parse_args()

    pilot = JevPilot(api_key=args.api_key)
    pilot.step(
        target=args.target,
        goal=args.goal,
        dry_run=not args.execute,
        text_to_type=args.type_text
    )


if __name__ == "__main__":
    main()
